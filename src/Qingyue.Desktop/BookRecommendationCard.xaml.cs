using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Media;
using System.Diagnostics;
using System.Windows.Threading;

namespace EpubKindleFix;

public partial class BookRecommendationCard : UserControl
{
    public static readonly DependencyProperty BookProperty = DependencyProperty.Register(nameof(Book),
        typeof(DiscoveryBook), typeof(BookRecommendationCard), new PropertyMetadata(null, BookChanged));
    public static readonly DependencyProperty ReasonProperty = DependencyProperty.Register(nameof(Reason),
        typeof(string), typeof(BookRecommendationCard), new PropertyMetadata("", ReasonChanged));
    public static readonly RoutedEvent SearchRequestedEvent = EventManager.RegisterRoutedEvent(nameof(SearchRequested),
        RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(BookRecommendationCard));
    public static readonly RoutedEvent DismissRequestedEvent = EventManager.RegisterRoutedEvent(nameof(DismissRequested),
        RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(BookRecommendationCard));

    public DiscoveryBook? Book { get => (DiscoveryBook?)GetValue(BookProperty); set => SetValue(BookProperty, value); }
    public string Reason { get => (string)GetValue(ReasonProperty); set => SetValue(ReasonProperty, value); }
    public event RoutedEventHandler SearchRequested { add => AddHandler(SearchRequestedEvent, value); remove => RemoveHandler(SearchRequestedEvent, value); }
    public event RoutedEventHandler DismissRequested { add => AddHandler(DismissRequestedEvent, value); remove => RemoveHandler(DismissRequestedEvent, value); }
    public static readonly RoutedEvent WishRequestedEvent = EventManager.RegisterRoutedEvent(nameof(WishRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(BookRecommendationCard));
    public event RoutedEventHandler WishRequested { add => AddHandler(WishRequestedEvent, value); remove => RemoveHandler(WishRequestedEvent, value); }
    private void Wish_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(WishRequestedEvent, this));
    public void MarkWanted() => SetWanted(true);
    public void SetWanted(bool wanted)
    {
        WishButton.Content = wanted ? "✓ 已加入想读" : "＋ 想读";
        WishButton.ToolTip = wanted ? "再次点击可取消想读" : "加入想读清单";
        if (wanted)
        {
            // Keep the same template and spring transforms when selection changes.
            WishButton.SetResourceReference(Control.BackgroundProperty, "WantedButtonFill");
            WishButton.SetResourceReference(Control.ForegroundProperty, "WantedButtonText");
            WishButton.SetResourceReference(Control.BorderBrushProperty, "WantedButtonFill");
        }
        else
        {
            WishButton.ClearValue(Control.BackgroundProperty);
            WishButton.ClearValue(Control.ForegroundProperty);
            WishButton.ClearValue(Control.BorderBrushProperty);
        }
    }
    private bool pointerInside;
    private bool pointerFocus;
    private bool suppressRestoredFocus;
    private bool restoringWindowFocus;
    private Window? hostWindow;
    private bool showingDetails;
    private readonly SpringValue pageSpring = new(40, 11);
    private readonly MeshGeometry3D pageMesh;
    private readonly MeshGeometry3D firstLeafMesh;
    private readonly MeshGeometry3D secondLeafMesh;
    private readonly MeshGeometry3D edgeMesh;
    private CancellationTokenSource? descriptionCancellation;
    private bool descriptionLoaded;
    private double openingShift;
    private bool rendering;
    private long lastFrameTick;

    public BookRecommendationCard()
    {
        InitializeComponent();
        pageMesh = BookPageGeometry.Create();
        firstLeafMesh = BookPageGeometry.Create(BookPageLayer.FirstLeaf);
        secondLeafMesh = BookPageGeometry.Create(BookPageLayer.SecondLeaf);
        edgeMesh = BookPageGeometry.CreateEdge(pageMesh);
        PageSurface.Geometry = PageBack.Geometry = pageMesh;
        FirstLeaf.Geometry = firstLeafMesh;
        SecondLeaf.Geometry = secondLeafMesh;
        CoverEdge.Geometry = edgeMesh;
        Loaded += (_, _) => { AttachWindow(); Appearance.ApplyWindow(this); UpdatePage(animate: false); };
        Unloaded += (_, _) => { DetachWindow(); ResetInteraction(animate: false); descriptionCancellation?.Cancel(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) ResetInteraction(animate: false); else UpdatePage(animate: false); };
    }

    private void AttachWindow()
    {
        var owner = Window.GetWindow(this);
        if (ReferenceEquals(owner, hostWindow)) return;
        DetachWindow(); hostWindow = owner; restoringWindowFocus = false;
        if (hostWindow is null) return;
        hostWindow.Deactivated += Host_Deactivated;
        hostWindow.Activated += Host_Activated;
    }

    private void DetachWindow()
    {
        if (hostWindow is null) return;
        hostWindow.Deactivated -= Host_Deactivated;
        hostWindow.Activated -= Host_Activated;
        hostWindow = null;
    }

    private void ResetInteraction(bool animate)
    {
        pointerInside = false;
        suppressRestoredFocus = true;
        FocusOutline.Visibility = Visibility.Collapsed;
        UpdatePage(animate);
    }

    private void Host_Deactivated(object? sender, EventArgs e)
    {
        restoringWindowFocus = true;
        // Native browser windows may consume MouseLeave. Clear the complete
        // interaction state so no open page survives the window switch.
        ResetInteraction(animate: false);
    }

    private void Host_Activated(object? sender, EventArgs e)
    {
        restoringWindowFocus = true;
        ResetInteraction(animate: false);
        var owner = hostWindow;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsLoaded || !IsVisible || owner is null || !ReferenceEquals(owner, hostWindow) || !owner.IsActive) return;
            // Re-evaluate actual hover after WPF restores focus; do not treat a
            // restored action-button focus as fresh keyboard navigation.
            Mouse.Synchronize();
            pointerInside = HoverSurface.IsMouseOver;
            restoringWindowFocus = false;
            UpdatePage();
        }));
    }

    private static void BookChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (BookRecommendationCard)sender;
        if (args.NewValue is not DiscoveryBook book) return;
        card.SetWanted(false);
        card.SearchButton.Content = book.IsComic ? "去漫画搜索  ↗" : "去书库搜索  ↗";
        card.descriptionCancellation?.Cancel();
        card.descriptionLoaded = false;
        card.OutsideTitle.Text = card.InsideTitle.Text = card.FallbackTitle.Text = book.Title;
        card.InsideAuthor.Text = card.FallbackAuthor.Text = book.Author;
        card.InsideDescription.Text = book.Description;
        card.OutsideGenre.Text = book.GenreLabel;
        card.OutsideTitle.ToolTip = book.Title;
        card.InsideDescription.ToolTip = book.Description;
        card.InsideAuthor.ToolTip = book.Author;
        System.Windows.Automation.AutomationProperties.SetName(card, book.Title + "，" + book.Author);
        card.CoverImage.Source = null;
        card.CoverImage.Visibility = Visibility.Collapsed;
        card.FallbackCover.Visibility = Visibility.Visible;
        card.CoverStatus.Text = "封面加载中";
        _ = card.LoadCoverAsync(book);
    }

    private async Task LoadCoverAsync(DiscoveryBook book)
    {
        System.Windows.Media.Imaging.BitmapSource? cover;
        try { cover = await BookCoverStore.LoadAsync(book).ConfigureAwait(false); }
        catch { cover = null; }
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (Book != book) return;
                if (cover is null) { CoverStatus.Text = "封面暂未获取"; return; }
                CoverImage.Source = cover;
                CoverImage.Visibility = Visibility.Visible;
                FallbackCover.Visibility = Visibility.Collapsed;
            });
        }
        catch (OperationCanceledException) { /* The application closed during loading. */ }
        catch (InvalidOperationException) { /* The dispatcher is already shutting down. */ }
    }

    private static void ReasonChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((BookRecommendationCard)sender).OutsideGenre.ToolTip = args.NewValue;

    private async Task LoadDescriptionAsync(DiscoveryBook book)
    {
        descriptionCancellation?.Cancel();
        descriptionCancellation?.Dispose();
        descriptionCancellation = new();
        var cancellation = descriptionCancellation.Token;
        try
        {
            await Task.Delay(250, cancellation);
            var description = await OpenLibraryCatalog.DescriptionAsync(book.WorkKey!, cancellation);
            if (cancellation.IsCancellationRequested || Book != book || !IsLoaded) return;
            InsideDescription.Text = description ?? book.Description.Replace("作品简介正在加载。", "书目来源：Open Library。点击搜索查看可用版本。");
            InsideDescription.ToolTip = InsideDescription.Text;
            descriptionLoaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            if (Book == book && IsLoaded) InsideDescription.Text = book.Description.Replace("作品简介正在加载。", "简介暂时无法加载，可点击搜索了解这本书。");
        }
    }

    private void Cover_MouseEnter(object sender, MouseEventArgs e)
    { if (hostWindow?.IsActive == false) return; pointerInside = true; UpdatePage(); }
    private void Cover_MouseLeave(object sender, MouseEventArgs e) { pointerInside = false; UpdatePage(); }
    private void Card_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A mouse click leaves keyboard focus on the action button. It must
        // not keep the book open after the pointer leaves the cover.
        pointerFocus = true;
        FocusOutline.Visibility = Visibility.Collapsed;
        UpdatePage();
    }
    private void FocusWithin_Changed(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (!IsKeyboardFocusWithin) pointerFocus = false;
        else if (!restoringWindowFocus && hostWindow?.IsActive != false && InputManager.Current.MostRecentInputDevice is KeyboardDevice)
        { pointerFocus = false; suppressRestoredFocus = false; }
        FocusOutline.Visibility = IsKeyboardFocusWithin && !pointerFocus && !suppressRestoredFocus ? Visibility.Visible : Visibility.Collapsed;
        UpdatePage();
    }

    private void UpdatePage(bool animate = true)
    {
        var open = IsLoaded && IsVisible && hostWindow?.IsActive != false
            && (pointerInside || IsKeyboardFocusWithin && !pointerFocus && !suppressRestoredFocus);
        if (animate && open == showingDetails) return;
        showingDetails = open;
        if (open) openingShift = CalculateOpeningShift();
        if (open && !descriptionLoaded && Book?.WorkKey is not null) _ = LoadDescriptionAsync(Book);
        pageSpring.SetTuning(40, Appearance.Motion == "轻柔" ? 14 : 11);
        pageSpring.Target = open ? 1 : 0;
        Introduction.IsHitTestVisible = open;
        KeyboardNavigation.SetTabNavigation(Introduction, open ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        if (!animate || !Appearance.Animate)
        {
            StopRendering();
            pageSpring.Reset(open ? 1 : 0);
            ApplyFrame(pageSpring.Position);
            SetLayer(open);
            return;
        }
        SetLayer(true);
        if (rendering) return;
        rendering = true;
        lastFrameTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += RenderFrame;
    }

    private void RenderFrame(object? sender, EventArgs args)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (double)(now - lastFrameTick) / Stopwatch.Frequency;
        if (elapsed < 0.001) return;
        lastFrameTick = now;
        pageSpring.Step(elapsed);
        ApplyFrame(pageSpring.Position);
        if (!pageSpring.IsAtRest) return;
        StopRendering();
        if (!showingDetails) SetLayer(false);
    }

    private void StopRendering()
    {
        if (!rendering) return;
        CompositionTarget.Rendering -= RenderFrame;
        rendering = false;
    }

    private void SetLayer(bool raised)
    {
        var container = VisualTreeHelper.GetParent(this) as ContentPresenter;
        Panel.SetZIndex(container ?? (UIElement)this, raised ? 20 : 0);
    }

    private void ApplyFrame(double progress)
    {
        BookPageGeometry.Update(pageMesh, progress);
        BookPageGeometry.Update(firstLeafMesh, progress, BookPageLayer.FirstLeaf);
        BookPageGeometry.Update(secondLeafMesh, progress, BookPageLayer.SecondLeaf);
        BookPageGeometry.UpdateEdge(edgeMesh, pageMesh);
        var p = Math.Clamp(progress, 0, 1);
        var reveal = Math.Clamp((p - 0.24) / 0.55, 0, 1);
        Introduction.Opacity = reveal * reveal * (3 - 2 * reveal);
        IntroOffset.X = 8 * (1 - Introduction.Opacity);
        // The fold shadow travels with the page's crease (mirrors the
        // geometry's crease sweep 0.92 → 0.12 across the 160px page).
        var ease = p * p * (3 - 2 * p);
        var crease = 0.92 - 0.80 * ease;
        var bend = Math.Sin(Math.PI * p);
        FoldShadowShift.X = crease * 160 - 24;
        FoldShadow.Opacity = 0.08 * p + 0.42 * bend;
        FoldShadow.Width = 12 + 30 * bend;
        BookShadow.BlurRadius = 13 + p * 10;
        BookShadow.ShadowDepth = 4 + p * 6;
        BookShadow.Opacity = 0.13 + p * 0.06;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var lift = -6 * progress;
        BookLift.X = openingShift * progress;
        BookLift.Y = pageSpring.IsAtRest ? Math.Round(lift * dpi) / dpi : lift;
        BookScale.ScaleX = BookScale.ScaleY = 1 + 0.014 * bend;
    }

    private double CalculateOpeningShift()
    {
        // Unfold within the page even when the first book is close to the window edge.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer)
                return Math.Clamp(176 - HoverSurface.TranslatePoint(new Point(), viewer).X, 0, 140);
        return 0;
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        // Close before raising the event, because its async handler can create
        // and activate a browser window synchronously on the first invocation.
        ResetInteraction(animate: true);
        RaiseEvent(new RoutedEventArgs(SearchRequestedEvent, this));
    }
    private void Dismiss_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(DismissRequestedEvent, this));
    private void Card_KeyDown(object sender, KeyEventArgs e)
    {
        restoringWindowFocus = false;
        pointerFocus = false;
        suppressRestoredFocus = false;
        FocusOutline.Visibility = IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
        UpdatePage();
        if (ReferenceEquals(e.OriginalSource, this) && e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            ResetInteraction(animate: true);
            RaiseEvent(new RoutedEventArgs(SearchRequestedEvent, this));
        }
    }
}
