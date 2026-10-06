using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using Microsoft.Data.Sqlite;

namespace MiCamera.Net.RTSP.Services;

/// <summary>Stores the entire configuration in one SQLite transaction.</summary>
public sealed class ApplicationSettingsStore
{
    private readonly string _connectionString;
    private readonly MiCameraServerOptions _server;
    private readonly MiCameraRtspOptions _media;
    private long _activeVersion;
    public long ActiveVersion => Interlocked.Read(ref this._activeVersion);

    public ApplicationSettingsStore(string directory, MiCameraServerOptions server, MiCameraRtspOptions media)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string path = Path.Combine(directory, "settings.db");
        this._connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        this._server = server;
        this._media = media;
        using SqliteConnection connection = this.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        long version = (long)command.ExecuteScalar()!;
        if (version is not (0 or 1))
        {
            throw new IOException("不支持此 SQLite 配置版本，请使用对应版本的服务。");
        }
        if (version == 0)
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            if ((long)command.ExecuteScalar()! != 0)
            {
                throw new IOException("SQLite 配置结构无效，不会覆盖已有数据。");
            }
            command.CommandText = """
                BEGIN;
                CREATE TABLE Settings (Id INTEGER PRIMARY KEY CHECK (Id=1), Version INTEGER NOT NULL,
                    MilocoBaseUrl TEXT NOT NULL, MilocoPasswordMd5 TEXT NOT NULL,
                    RtspUsername TEXT NOT NULL, RtspDigestHa1 TEXT NOT NULL);
                CREATE TABLE Streams (StreamId TEXT PRIMARY KEY COLLATE NOCASE, CameraDeviceId TEXT NOT NULL,
                    Channel INTEGER NOT NULL CHECK (Channel>=0), Codec TEXT NOT NULL CHECK (Codec IN ('H264','H265')),
                    NominalFrameRate REAL NOT NULL CHECK (NominalFrameRate BETWEEN 1 AND 120),
                    UNIQUE (CameraDeviceId, Channel));
                PRAGMA user_version=1;
                COMMIT;
                """;
            command.ExecuteNonQuery();
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        SavedApplicationSettings? saved = this.Read();
        if (saved is not null)
        {
            SetupSettingsService.ValidateSaved(saved);
            this.Apply(saved);
            this.MarkActive(saved.Version);
            server.Initialization.Complete();
        }
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(this._connectionString);
        connection.Open();
        return connection;
    }

    public SavedApplicationSettings? Read()
    {
        using SqliteConnection connection = this.Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Version, MilocoBaseUrl, MilocoPasswordMd5, RtspUsername, RtspDigestHa1 FROM Settings WHERE Id=1";
        SavedApplicationSettings saved;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }
            saved = new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), []);
        }
        command.CommandText = "SELECT StreamId, CameraDeviceId, Channel, Codec, NominalFrameRate FROM Streams ORDER BY rowid";
        using SqliteDataReader streams = command.ExecuteReader();
        while (streams.Read())
        {
            saved.Streams.Add(new()
            {
                StreamId = streams.GetString(0), CameraDeviceId = streams.GetString(1), Channel = streams.GetInt32(2),
                Codec = Enum.Parse<MiCamera.Net.Abstractions.Common.Enums.VideoCodec>(streams.GetString(3)), NominalFrameRate = streams.GetDouble(4)
            });
        }
        return saved;
    }

    public SavedApplicationSettings Save(SavedApplicationSettings settings, long expectedVersion)
    {
        using SqliteConnection connection = this.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Version FROM Settings WHERE Id=1";
        long current = command.ExecuteScalar() is long value ? value : 0;
        if (current != expectedVersion)
        {
            throw new SettingsConflictException();
        }
        SavedApplicationSettings saved = settings with { Version = checked(current + 1) };
        command.CommandText = """
            INSERT INTO Settings VALUES (1,$version,$baseUrl,$miloco,$username,$digest)
            ON CONFLICT(Id) DO UPDATE SET Version=$version,MilocoBaseUrl=$baseUrl,
                MilocoPasswordMd5=$miloco,RtspUsername=$username,RtspDigestHa1=$digest;
            DELETE FROM Streams;
            """;
        command.Parameters.AddWithValue("$version", saved.Version);
        command.Parameters.AddWithValue("$baseUrl", saved.MilocoBaseUrl);
        command.Parameters.AddWithValue("$miloco", saved.MilocoPasswordMd5);
        command.Parameters.AddWithValue("$username", saved.RtspUsername);
        command.Parameters.AddWithValue("$digest", saved.RtspDigestHa1);
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO Streams VALUES ($id,$did,$channel,$codec,$fps)";
        foreach (CameraStreamOptions stream in saved.Streams)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", stream.StreamId);
            command.Parameters.AddWithValue("$did", stream.CameraDeviceId);
            command.Parameters.AddWithValue("$channel", stream.Channel);
            command.Parameters.AddWithValue("$codec", stream.Codec.ToString());
            command.Parameters.AddWithValue("$fps", stream.NominalFrameRate);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return saved;
    }

    public void Apply(SavedApplicationSettings saved)
    {
        this._server.Miloco.BaseUrl = saved.MilocoBaseUrl;
        this._server.Miloco.Username = "admin";
        this._server.Miloco.Password = saved.MilocoPasswordMd5;
        this._server.Streams = saved.Streams;
        this._media.Rtsp.Username = saved.RtspUsername;
        this._media.Rtsp.DigestHa1 = saved.RtspDigestHa1;
    }

    public void MarkActive(long version) => Interlocked.Exchange(ref this._activeVersion, version);
}
