using Microsoft.UI.Xaml.Navigation;

namespace YandexMusicSaver
{
    public partial class App : Application
    {
        static App()
        {
            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        }

        private MainWindow? m_window;

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            m_window = new MainWindow();
            m_window.Activate();
        }
    }
}
