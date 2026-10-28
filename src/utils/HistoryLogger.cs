using System.IO;
using System.Text;
using System.Globalization;
using Microsoft.Data.Sqlite;
using CsvHelper;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class SQLiteHistoryLogger
    {
        public static readonly string CONNECTION_STRING = $"Data Source={AppPaths.HistoryDb};";

        private static SqliteConnection _sharedConnection;
        private static readonly object _connectionLock = new object();

        static SQLiteHistoryLogger()
        {
            InitializeDatabase();
        }

        private static void InitializeDatabase()
        {
            using (var command = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS TranslationHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT,
                    SourceText TEXT,
                    TranslatedText TEXT,
                    TargetLanguage TEXT,
                    ApiUsed TEXT
                );", GetConnection()))
            {
                command.ExecuteNonQuery();
            }
        }

        private static SqliteConnection GetConnection()
        {
            lock (_connectionLock)
            {
                if (_sharedConnection == null)
                {
                    _sharedConnection = new SqliteConnection(CONNECTION_STRING);
                    _sharedConnection.Open();
                }
                else if (_sharedConnection.State != System.Data.ConnectionState.Open)
                {
                    try
                    {
                        _sharedConnection.Open();
                    }
                    catch
                    {
                        _sharedConnection.Dispose();
                        _sharedConnection = new SqliteConnection(CONNECTION_STRING);
                        _sharedConnection.Open();
                    }
                }

                return _sharedConnection;
            }
        }

        private static TranslationHistoryEntry ReadEntry(System.Data.Common.DbDataReader reader, DateTime localTime, string timestampFormat)
        {
            return new TranslationHistoryEntry
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                Timestamp = localTime.ToString(timestampFormat),
                TimestampFull = localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
            };
        }

        public static async Task LogTranslation(string sourceText, string translatedText,
            string targetLanguage, string apiUsed, CancellationToken token = default)
        {
            string insertQuery = @"
                INSERT INTO TranslationHistory (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed)
                VALUES (@Timestamp, @SourceText, @TranslatedText, @TargetLanguage, @ApiUsed)";

            using (var command = new SqliteCommand(insertQuery, GetConnection()))
            {
                command.Parameters.AddWithValue("@Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("@SourceText", sourceText);
                command.Parameters.AddWithValue("@TranslatedText", translatedText);
                command.Parameters.AddWithValue("@TargetLanguage", targetLanguage);
                command.Parameters.AddWithValue("@ApiUsed", apiUsed);
                await command.ExecuteNonQueryAsync(token);
            }
        }

        public static async Task<(List<TranslationHistoryEntry>, int)> LoadHistoryAsync(
            int page, int maxRow, string searchText, CancellationToken token = default)
        {
            var history = new List<TranslationHistoryEntry>();
            int totalCount = 0;
            using (var command = new SqliteCommand(@"
                SELECT COUNT(*) 
                FROM TranslationHistory
                WHERE SourceText LIKE @search OR TranslatedText LIKE @search", GetConnection()))

            {
                command.Parameters.AddWithValue("@search", $"%{searchText}%");
                totalCount = Convert.ToInt32(await command.ExecuteScalarAsync(token));
            }

            // 计算最大页数，至少为 1
            int maxPage = Math.Max(1, (int)Math.Ceiling(totalCount / (double)maxRow));
            int offset = Math.Max(0, (page - 1) * maxRow);

            using (var command = new SqliteCommand(@"
                SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                FROM TranslationHistory
                WHERE SourceText LIKE @search OR TranslatedText LIKE @search
                ORDER BY Timestamp DESC
                LIMIT @maxRow OFFSET @offset", GetConnection()))

            {
                command.Parameters.AddWithValue("@search", $"%{searchText}%");
                command.Parameters.AddWithValue("@maxRow", maxRow);
                command.Parameters.AddWithValue("@offset", offset);

                using (var reader = await command.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
                        DateTime localTime;
                        try
                        {
                            localTime = DateTimeOffset.FromUnixTimeSeconds((long)Convert.ToDouble(unixTime)).LocalDateTime;
                        }
                        catch (FormatException)
                        {
                            // DEPRECATED
                            await MigrateOldTimestampFormat();
                            return await LoadHistoryAsync(page, maxRow, searchText, token);
                        }
                        history.Add(ReadEntry(reader, localTime, "yyyy-MM-dd HH:mm"));
                    }
                }
            }
            return (history, maxPage);
        }

        public static async Task ClearHistory(CancellationToken token = default)
        {
            string selectQuery = "DELETE FROM TranslationHistory; DELETE FROM sqlite_sequence WHERE NAME='TranslationHistory'";
            using (var command = new SqliteCommand(selectQuery, GetConnection()))
            {
                await command.ExecuteNonQueryAsync(token);
            }
        }

        public static async Task DeleteRows(IEnumerable<long> ids)
        {
            using var connection = new SqliteConnection(CONNECTION_STRING);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            foreach (long id in ids.Distinct())
            {
                using var command = new SqliteCommand("DELETE FROM TranslationHistory WHERE Id = @id", connection, transaction);
                command.Parameters.AddWithValue("@id", id);
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        public static async Task<TranslationHistoryEntry?> LoadLastTranslation(CancellationToken token = default)
        {
            string selectQuery = @"
                SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                FROM TranslationHistory
                ORDER BY Id DESC
                LIMIT 1";

            using (var command = new SqliteCommand(selectQuery, GetConnection()))
            using (var reader = await command.ExecuteReaderAsync(token))
            {
                if (await reader.ReadAsync(token))
                {
                    string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
                    DateTime localTime = DateTimeOffset.FromUnixTimeSeconds((long)Convert.ToDouble(unixTime)).LocalDateTime;
                    return ReadEntry(reader, localTime, "yyyy-MM-dd HH:mm");
                }
                return null;
            }
        }

        public static async Task ExportToCSV(string filePath, CancellationToken token = default)
        {
            var history = new List<TranslationHistoryEntry>();

            string selectQuery = @"
                SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                FROM TranslationHistory
                ORDER BY Timestamp DESC";

            using (var command = new SqliteCommand(selectQuery, GetConnection()))
            using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
                    DateTime localTime = DateTimeOffset.FromUnixTimeSeconds((long)Convert.ToDouble(unixTime)).LocalDateTime;
                    history.Add(ReadEntry(reader, localTime, "yyyy-MM-dd HH:mm:ss"));
                }
            }

            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
            using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
            await csvWriter.WriteRecordsAsync(history, token);
        }

        // DEPRECATED
        private static async Task MigrateOldTimestampFormat()
        {
            var records = new List<(long id, string timestamp)>();
            using (var command = new SqliteCommand("SELECT Id, Timestamp FROM TranslationHistory", GetConnection()))
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    long id = reader.GetInt64(reader.GetOrdinal("Id"));
                    string timestamp = reader.GetString(reader.GetOrdinal("Timestamp"));
                    records.Add((id, timestamp));
                }
            }

            foreach (var (id, timestamp) in records)
            {
                if (DateTime.TryParse(timestamp, out DateTime dt))
                {
                    long unixTime = ((DateTimeOffset)dt).ToUnixTimeSeconds();
                    using var updateCommand = new SqliteCommand(
                        "UPDATE TranslationHistory SET Timestamp = @Timestamp WHERE Id = @Id",
                        GetConnection());
                    updateCommand.Parameters.AddWithValue("@Id", id);
                    updateCommand.Parameters.AddWithValue("@Timestamp", unixTime.ToString());
                    await updateCommand.ExecuteNonQueryAsync();
                }
            }
        }
    }
}
