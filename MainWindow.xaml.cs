using Microsoft.UI.Xaml;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Threading;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Composition;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.Effects;
using Microsoft.UI.Windowing;

namespace YandexMusicSaver;

public class UrlItem : INotifyPropertyChanged
{
    private string _url = "";
    public string Url
    {
        get => _url;
        set
        {
            if (_url != value)
            {
                _url = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Url)));
            }
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class MainWindow : Window
{
    private readonly DownloadEngine _downloadEngine;
    private CancellationTokenSource? _cancellationTokenSource;
    private ObservableCollection<UrlItem> _urlItems;
    private string? _currentThumbnailUrl;
    private Compositor? _compositor;
    private ContainerVisual? _coverContainer;
    private SpriteVisual? _currentCoverVisual;
    private int _coverRequestVersion;

    public MainWindow()
    {
        this.InitializeComponent();
        
        _urlItems = new ObservableCollection<UrlItem> { new UrlItem() };
        UrlsList.ItemsSource = _urlItems;
        
        FolderTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads\\YandexMusic";
        
        _downloadEngine = new DownloadEngine();
        _downloadEngine.ProgressChanged += DownloadEngine_ProgressChanged;
        
        ExtendsContentIntoTitleBar = true;
        SizeChanged += (_, _) => UpdateBackgroundCoverSize();
        BackgroundCover.Loaded += (_, _) => EnsureCoverContainer();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow.GetFromWindowId(windowId).SetIcon("Assets\\AppIcon.ico");

        _wndProcDelegate = CustomWndProc;
        _oldWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, _wndProcDelegate);

    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate _wndProcDelegate;
    private IntPtr _oldWndProc;

    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private IntPtr CustomWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            uint dpi = GetDpiForWindow(hwnd);
            float scalingFactor = (float)dpi / 96f;
            
            MINMAXINFO minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            minMaxInfo.ptMinTrackSize.x = (int)(600 * scalingFactor);
            minMaxInfo.ptMinTrackSize.y = (int)(400 * scalingFactor);
            Marshal.StructureToPtr(minMaxInfo, lParam, true);
        }
        return CallWindowProc(_oldWndProc, hwnd, msg, wParam, lParam);
    }

    private async void SelectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker();
        folderPicker.SuggestedStartLocation = PickerLocationId.Downloads;
        folderPicker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder != null)
        {
            FolderTextBox.Text = folder.Path;
        }
    }

    private void AddUrlButton_Click(object sender, RoutedEventArgs e)
    {
        _urlItems.Add(new UrlItem());
    }

    private void RemoveUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is UrlItem item)
        {
            if (_urlItems.Count > 1)
            {
                _urlItems.Remove(item);
            }
            else
            {
                item.Url = "";
            }
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource = null;
            DownloadButton.Content = "Начать загрузку";
            StatusTextBlock.Text = "Отмена...";
            return;
        }

        var urls = _urlItems.Select(x => x.Url.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
        string folder = FolderTextBox.Text.Trim();
        
        if (urls.Count == 0)
        {
            TrackTitleTextBlock.Text = "Ошибка: добавьте хотя бы одну ссылку Яндекс Музыки.";
            ProgressContainer.Visibility = Visibility.Visible;
            return;
        }

        string browser = "None";
        if (BrowserComboBox.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            browser = (item.Tag?.ToString() ?? item.Content.ToString())!.ToLowerInvariant();
        }

        DownloadButton.Content = "Отменить загрузку";
        DownloadProgressBar.Value = 0;
        DownloadProgressBar.IsIndeterminate = true;
        ProgressContainer.Visibility = Visibility.Visible;
        TrackTitleTextBlock.Text = "Подготовка загрузки...";
        StatusTextBlock.Text = "0%";
        DetailsTextBlock.Text = "Подключение...";
        
        _cancellationTokenSource = new CancellationTokenSource();
        
        try
        {
            await _downloadEngine.DownloadAsync(urls, folder, browser, _cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            TrackTitleTextBlock.Text = "Непредвиденная ошибка";
            StatusTextBlock.Text = "Ошибка";
            DetailsTextBlock.Text = ex.Message;
        }
        finally
        {
            _cancellationTokenSource = null;
            DownloadButton.Content = "Начать загрузку";
        }
    }

    private void DownloadEngine_ProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (e.Percentage.HasValue)
            {
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressBar.Value = e.Percentage.Value;
            }
            else if (!string.IsNullOrEmpty(e.StatusText))
            {
                DownloadProgressBar.IsIndeterminate = true;
            }

            if (!string.IsNullOrEmpty(e.StatusText))
                StatusTextBlock.Text = e.StatusText;
            
            if (!string.IsNullOrEmpty(e.TrackTitle))
                TrackTitleTextBlock.Text = e.TrackTitle;
                
            if (!string.IsNullOrEmpty(e.DetailsText))
                DetailsTextBlock.Text = e.DetailsText;
                
            if (!string.IsNullOrEmpty(e.ThumbnailUrl) && _currentThumbnailUrl != e.ThumbnailUrl)
            {
                _currentThumbnailUrl = e.ThumbnailUrl;
                UpdateBackgroundThumbnailFromUrl(e.ThumbnailUrl);
            }
        });
    }

    private async void UpdateBackgroundThumbnailFromUrl(string imageUrl)
    {
        try
        {
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
                return;

            UpdateBackgroundCoverSize();
            ApplyBlurEffect(uri);
        }
        catch
        {
        }
    }

    private void UpdateBackgroundCoverSize()
    {
        double side = Math.Max(0, Bounds.Height - 96);
        BackgroundCover.Width = side;
        BackgroundCover.Height = side;

        var size = new System.Numerics.Vector2((float)side, (float)side);
        if (_coverContainer != null)
        {
            _coverContainer.Size = size;
            _coverContainer.Clip = CreateRoundedClip(side);

            foreach (var visual in _coverContainer.Children)
            {
                visual.Size = size;
            }
        }
    }

    private void EnsureCoverContainer()
    {
        if (_coverContainer != null)
            return;

        _compositor = ElementCompositionPreview.GetElementVisual(BackgroundCover).Compositor;
        _coverContainer = _compositor.CreateContainerVisual();
        _coverContainer.Opacity = 0f;
        ElementCompositionPreview.SetElementChildVisual(BackgroundCover, _coverContainer);
        UpdateBackgroundCoverSize();
    }

    private void ApplyBlurEffect(Uri imageUri)
    {
        EnsureCoverContainer();
        if (_compositor == null || _coverContainer == null)
            return;

        var requestVersion = ++_coverRequestVersion;
        var surface = LoadedImageSurface.StartLoadFromUri(imageUri);
        var surfaceBrush = _compositor.CreateSurfaceBrush(surface);
        surfaceBrush.Stretch = CompositionStretch.UniformToFill;
        surfaceBrush.CenterPoint = new System.Numerics.Vector2(0.5f, 0.5f);
        surfaceBrush.Scale = new System.Numerics.Vector2(1.035f, 1.035f);

        var blurEffect = new GaussianBlurEffect
        {
            Name = "CoverBlur",
            BlurAmount = 14f,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("source")
        };

        var effectFactory = _compositor.CreateEffectFactory(blurEffect);
        var effectBrush = effectFactory.CreateBrush();
        effectBrush.SetSourceParameter("source", surfaceBrush);

        var side = (float)Math.Max(0, Bounds.Height - 96);
        var nextVisual = _compositor.CreateSpriteVisual();
        nextVisual.Size = new System.Numerics.Vector2(side, side);
        nextVisual.Brush = effectBrush;
        nextVisual.Opacity = 0f;
        _coverContainer.Children.InsertAtTop(nextVisual);

        surface.LoadCompleted += (_, args) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (requestVersion != _coverRequestVersion || args.Status != LoadedImageSourceLoadStatus.Success)
                {
                    try { _coverContainer.Children.Remove(nextVisual); } catch { }
                    return;
                }

                StartSmoothFade(nextVisual, 1f, 850);

                var previousVisual = _currentCoverVisual;
                if (previousVisual != null)
                {
                    StartSmoothFade(previousVisual, 0f, 700);
                    RemoveCoverVisualAfterDelay(previousVisual, 900);
                }
                else
                {
                    StartSmoothFade(_coverContainer, 1f, 700);
                    BackgroundCover.Opacity = 1;
                }

                _currentCoverVisual = nextVisual;
            });
        };
    }

    private async void RemoveCoverVisualAfterDelay(Visual visual, int delayMs)
    {
        await System.Threading.Tasks.Task.Delay(delayMs);
        DispatcherQueue.TryEnqueue(() =>
        {
            try { _coverContainer?.Children.Remove(visual); } catch { }
        });
    }

    private void StartSmoothFade(Visual visual, float targetOpacity, int durationMs)
    {
        if (_compositor == null)
            return;

        var easing = _compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.16f, 1.0f),
            new System.Numerics.Vector2(0.3f, 1.0f));

        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, targetOpacity, easing);
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation(nameof(Visual.Opacity), animation);
    }

    private CompositionClip? CreateRoundedClip(double side)
    {
        if (_compositor == null)
            return null;

        var geometry = _compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new System.Numerics.Vector2((float)side, (float)side);
        geometry.CornerRadius = new System.Numerics.Vector2(18, 18);
        var clip = _compositor.CreateGeometricClip(geometry);
        return clip;
    }
}
