namespace TranslucentTodo.Services
{
    public sealed class AppSettings
    {
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double Width { get; set; } = 320;
        public double Height { get; set; } = 460;
        public bool StartWithWindows { get; set; } = true;
        public bool UpcomingExpanded { get; set; }
    }

    public static class SettingsStore
    {
        private const string FileName = "settings.json";

        public static AppSettings Load() => JsonFile.Load<AppSettings>(FileName) ?? new AppSettings();

        public static void Save(AppSettings settings) => JsonFile.Save(FileName, settings);
    }
}
