using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Gml.Models.System;
using GmlCore.Interfaces.Enums;
using GmlCore.Interfaces.Launcher;
using GmlCore.Interfaces.Procedures;
using GmlCore.Interfaces.Storage;
using GmlCore.Interfaces.System;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace Gml.Core.Helpers.Files;

public class FileStorageProcedures : IFileStorageProcedures
{
    private static readonly string[] StorageBuckets = { "profiles", "profile-backgrounds" };

    private readonly IBugTrackerProcedures _bugTracker;
    private readonly ILauncherInfo _launcherInfo;
    private readonly IStorageService _storage;
    private IMinioClient? _minioClient;
    private bool? _objectTaggingSupported;

    public FileStorageProcedures(ILauncherInfo launcherInfo, IStorageService storage,
        IBugTrackerProcedures bugTracker)
    {
        _launcherInfo = launcherInfo;
        _storage = storage;
        _bugTracker = bugTracker;

        launcherInfo.SettingsUpdated.Subscribe(SettingsUpdated);
    }

    internal IMinioClient MinioClient => _minioClient ??= CreateMinioClient();

    private IMinioClient CreateMinioClient()
    {
        var settings = _launcherInfo.StorageSettings;
        var (endpoint, useSsl) = ParseStorageEndpoint(settings.StorageHost);

        return new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(settings.StorageLogin, settings.StoragePassword)
            .WithSSL(useSsl)
            .Build();
    }

    /// <summary>
    ///     Приводит значение поля "Хост хранилища" к виду, который понимает Minio-клиент.
    ///     Схема (или порт) определяет, использовать ли HTTPS: R2, AWS S3 и большинство
    ///     публичных S3-сервисов работают только по TLS, локальный MinIO — обычно по HTTP.
    /// </summary>
    internal static (string Endpoint, bool UseSsl) ParseStorageEndpoint(string? storageHost)
    {
        var value = (storageHost ?? string.Empty).Trim();
        var useSsl = true;

        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["https://".Length..];
            useSsl = true;
        }
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["http://".Length..];
            useSsl = false;
        }
        else
        {
            var forcedScheme = Environment.GetEnvironmentVariable("GML_STORAGE_SCHEME");

            if (!string.IsNullOrWhiteSpace(forcedScheme))
            {
                useSsl = !forcedScheme.Trim().Equals("http", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // Без схемы: порт (кроме 443) считаем локальным MinIO по http,
                // отсутствие порта — публичным S3-совместимым сервисом по https.
                var portSeparator = value.LastIndexOf(':');
                useSsl = portSeparator <= 0
                         || !int.TryParse(value[(portSeparator + 1)..], out var port)
                         || port == 443;
            }
        }

        // На случай, если в поле вставили полный URL с бакетом/путём — оставляем только хост.
        var pathSeparator = value.IndexOf('/');

        if (pathSeparator >= 0)
            value = value[..pathSeparator];

        return (value.TrimEnd('/'), useSsl);
    }

    private static (string Bucket, string Prefix) SplitFolder(string? folder)
    {
        var value = string.IsNullOrWhiteSpace(folder)
            ? "other"
            : folder!.Trim().Replace('\\', '/').Trim('/');
        var separator = value.IndexOf('/');

        return separator < 0
            ? (value, string.Empty)
            : (value[..separator], value[(separator + 1)..]);
    }

    public async Task<IFileInfo?> DownloadFileStream(
        string fileHash,
        Stream outputStream)
    {
        LocalFileInfo? localFileInfo = default;

        switch (_launcherInfo.StorageSettings.StorageType)
        {
            case StorageType.LocalStorage:
                localFileInfo = await _storage.GetAsync<LocalFileInfo>(fileHash).ConfigureAwait(false);

                if (localFileInfo is null) return localFileInfo;

                if (Path.GetFileName(localFileInfo?.FullPath) is { } fileName &&
                    Guid.TryParse(fileName, out var guid) && localFileInfo!.FullPath.Contains("Attachments"))
                {
                    // Если это дополнительный файл
                }
                else if (localFileInfo != null) // Загрузка файлов minecraft
                {
                    // Если это файл профиля
                    localFileInfo.FullPath = Path.GetFullPath(string.Join("/", _launcherInfo.InstallationDirectory,
                        localFileInfo.Directory));
                }

                await using (var stream = new FileStream(localFileInfo!.FullPath, FileMode.Open))
                {
                    await stream.CopyToAsync(outputStream);
                }


                break;

            case StorageType.S3:

                try
                {
                    var (downloaded, _) = await TryDownloadFromStorageAsync(StorageBuckets, fileHash, outputStream)
                        .ConfigureAwait(false);

                    if (downloaded)
                        localFileInfo = new LocalFileInfo();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return localFileInfo;
    }

    public async Task<string> LoadFile(Stream fileStream,
        string? folder = null,
        string? defaultFileName = null,
        Dictionary<string, string>? tags = null)
    {
        var fileName = defaultFileName ?? Guid.NewGuid().ToString();

        switch (_launcherInfo.StorageSettings.StorageType)
        {
            case StorageType.LocalStorage:

                var filePath = Path.Combine(_launcherInfo.InstallationDirectory, "Attachments", fileName);

                await ConvertStreamToFile(fileStream, filePath);

                await _storage.SetAsync(fileName, new LocalFileInfo
                {
                    Name = fileName,
                    Directory = filePath.Replace(Path.GetFullPath(fileName), string.Empty),
                    FullPath = filePath
                });
                break;

            case StorageType.S3:

                var (bucketName, objectPrefix) = SplitFolder(folder);

                await CreateBuсketIfNotExists(bucketName);

                if (fileStream.Length > 0)
                {
                    var objectName = string.IsNullOrEmpty(objectPrefix)
                        ? fileName
                        : $"{objectPrefix}/{fileName}";

                    var putObjectArgs = new PutObjectArgs()
                        .WithContentType("application/octet-stream")
                        .WithObjectSize(fileStream.Length)
                        .WithStreamData(fileStream)
                        .WithBucket(bucketName)
                        .WithObject(objectName);

                    // Object tagging поддерживают не все S3-совместимые хранилища (например, Cloudflare R2),
                    // поэтому метаданные объекта (в т.ч. оригинальное имя файла) кладём в x-amz-meta-*.
                    if (tags is not null && tags.Count > 0)
                        putObjectArgs = putObjectArgs.WithHeaders(tags);

                    await MinioClient.PutObjectAsync(putObjectArgs).ConfigureAwait(false);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException();
        }

        return fileName;
    }

    public async Task<(Stream File, string fileName, long Length)> GetFileStream(string fileHash, string? backet = null)
    {
        Stream fileStream = new MemoryStream();
        var fileName = string.Empty;
        long length = 0;

        switch (_launcherInfo.StorageSettings.StorageType)
        {
            case StorageType.LocalStorage:
                var localFileInfo = await _storage.GetAsync<LocalFileInfo>(fileHash).ConfigureAwait(false);

                if (localFileInfo is not null && File.Exists(localFileInfo.FullPath))
                {
                    // If it's an additional file
                    if (Path.GetFileName(localFileInfo.FullPath) is { } name && Guid.TryParse(name, out _))
                    {
                        // fileStream = [ Something related to additional files ]
                    }
                    // If it is a Minecraft file

                    // fileStream = [ Something related to Minecraft files ]
                    fileStream = new FileStream(localFileInfo.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                    fileName = localFileInfo.Name;
                    length = new FileInfo(localFileInfo.FullPath).Length;
                }

                break;

            case StorageType.S3:
                try
                {
                    var buckets = string.IsNullOrWhiteSpace(backet)
                        ? StorageBuckets
                        : new[] { SplitFolder(backet).Bucket };

                    var (downloaded, storedFileName) =
                        await TryDownloadFromStorageAsync(buckets, fileHash, fileStream).ConfigureAwait(false);

                    if (downloaded)
                    {
                        fileName = storedFileName;
                        length = fileStream.Length;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException();
        }

        // Resetting the position for the caller to be able to read the stream from the beginning
        fileStream.Position = 0;
        return (fileStream, fileName, length);
    }

    public async Task<bool> CheckFileExists(string folder, string fileHash)
    {
        try
        {
            switch (_launcherInfo.StorageSettings.StorageType)
            {
                case StorageType.LocalStorage:
                    var localFileInfo = await _storage.GetAsync<LocalFileInfo>(fileHash).ConfigureAwait(false);

                    return localFileInfo is not null;

                case StorageType.S3:
                    var (checkBucket, checkPrefix) = SplitFolder(folder);
                    var checkObjectName = string.IsNullOrEmpty(checkPrefix)
                        ? fileHash
                        : $"{checkPrefix}/{fileHash}";

                    var fileCheck = new StatObjectArgs()
                        .WithBucket(checkBucket)
                        .WithObject(checkObjectName);

                    var data = await MinioClient.StatObjectAsync(fileCheck);

                    return data.Size != 0;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        catch (ObjectNotFoundException)
        {
            return false;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            _bugTracker.CaptureException(exception);
            return false;
        }

        return false;
    }

    private async Task<(bool Downloaded, string FileName)> TryDownloadFromStorageAsync(
        IEnumerable<string> buckets,
        string objectName,
        Stream outputStream)
    {
        foreach (var bucket in buckets)
        {
            ObjectStat? stat;

            try
            {
                stat = await MinioClient.StatObjectAsync(new StatObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(objectName)).ConfigureAwait(false);
            }
            catch (ObjectNotFoundException)
            {
                continue;
            }
            catch (BucketNotFoundException)
            {
                continue;
            }

            if (stat is null || stat.Size <= 0)
                continue;

            await MinioClient.GetObjectAsync(new GetObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(objectName)
                    .WithCallbackStream((stream, token) => stream.CopyToAsync(outputStream, token)))
                .ConfigureAwait(false);

            var fileName = await GetStoredFileNameAsync(bucket, objectName, stat).ConfigureAwait(false);

            return (true, string.IsNullOrWhiteSpace(fileName) ? objectName : fileName!);
        }

        return (false, string.Empty);
    }

    private async Task<string?> GetStoredFileNameAsync(string bucket, string objectName, ObjectStat stat)
    {
        if (stat.MetaData is not null
            && stat.MetaData.TryGetValue("file-name", out var fileName)
            && !string.IsNullOrWhiteSpace(fileName))
        {
            return fileName;
        }

        // Совместимость со старыми объектами, у которых имя файла хранилось в тегах (MinIO, AWS S3).
        // Cloudflare R2 теги объектов не поддерживает — запоминаем результат, чтобы не дёргать API зря.
        if (_objectTaggingSupported == false)
            return null;

        try
        {
            var tagging = await MinioClient.GetObjectTagsAsync(new GetObjectTagsArgs()
                .WithBucket(bucket)
                .WithObject(objectName)).ConfigureAwait(false);

            _objectTaggingSupported = true;

            return tagging?.Tags is not null && tagging.Tags.TryGetValue("file-name", out var tagFileName)
                ? tagFileName
                : null;
        }
        catch (Exception exception)
        {
            _objectTaggingSupported = false;
            Console.WriteLine(exception);

            return null;
        }
    }

    private void SettingsUpdated(IStorageSettings settings)
    {
        _objectTaggingSupported = null;

        switch (_launcherInfo.StorageSettings.StorageType)
        {
            case StorageType.LocalStorage:
                break;
            case StorageType.S3:
                _minioClient?.Dispose();
                _minioClient = null;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private async Task CreateBuсketIfNotExists(string bucketName)
    {
        var beArgs = new BucketExistsArgs().WithBucket(bucketName);
        var found = await MinioClient.BucketExistsAsync(beArgs).ConfigureAwait(false);

        if (!found)
        {
            var mbArgs = new MakeBucketArgs()
                .WithBucket(bucketName);

            await MinioClient.MakeBucketAsync(mbArgs).ConfigureAwait(false);
        }
    }

    private async Task ConvertStreamToFile(Stream input, string filePath)
    {
        var fileInfo = new FileInfo(filePath);

        if (!fileInfo.Directory!.Exists) fileInfo.Directory.Create();

        using (var fileStream = File.Create(filePath))
        {
            await input.CopyToAsync(fileStream);
        }
    }

    private async Task<string> ConvertStreamToBase64Async(Stream stream)
    {
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream).ConfigureAwait(false);
        var fileBytes = memoryStream.ToArray();
        return Convert.ToBase64String(fileBytes);
    }
}
