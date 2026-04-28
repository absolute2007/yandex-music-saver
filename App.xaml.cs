using Microsoft.UI.Xaml.Navigation;

namespace YandexMusicSaver
{
    public partial class App : Application
    {
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
