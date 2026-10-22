using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using TranslucentTodo.Models;

namespace TranslucentTodo.Services
{
    /// <summary>Reads and writes small JSON files under %APPDATA%\TranslucentTodo.</summary>
    public static class JsonFile
    {
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TranslucentTodo");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static T? Load<T>(string fileName) where T : class
        {
            var path = Path.Combine(Folder, fileName);
            if (!File.Exists(path)) return null;

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
                }
                catch (IOException) when (attempt < 5)
                {
                    // Briefly locked (antivirus, cloud sync at sign-in): wait and retry rather than
                    // starting empty, because the next save would overwrite the real data.
                    Thread.Sleep(200);
                }
                catch (Exception)
                {
                    // Unreadable or corrupt: keep a copy before the app starts over with an empty file.
                    try
                    {
                        File.Copy(path, $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
                    }
                    catch (Exception)
                    {
                    }
                    return null;
                }
            }
        }

        /// <summary>Writes to a temp file first so a crash mid-write can't corrupt the data.</summary>
        public static void Save<T>(string fileName, T value)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var path = Path.Combine(Folder, fileName);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
                File.Move(tmp, path, overwrite: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static class TaskStore
    {
        private const string FileName = "tasks.json";

        public static List<TaskItem> Load() => JsonFile.Load<List<TaskItem>>(FileName) ?? new List<TaskItem>();

        public static void Save(IEnumerable<TaskItem> tasks) => JsonFile.Save(FileName, tasks);
    }
}
