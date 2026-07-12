using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DeskFader.Core;
using Microsoft.Win32;

namespace DeskFader.Settings;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DeskFaderService service;
    private string status = "Starting DeskFader";
    private bool startAtLogin;
    private Border? settingsDrawer;

    public MainWindow(DeskFaderService service)
    {
        this.service = service;
        Channels = new ObservableCollection<FaderChannel>(Enumerable.Range(1, DeskFaderConstants.SlotCount).Select(number => new FaderChannel(number)));
        Slots = new ObservableCollection<SlotRow>(Enumerable.Range(1, DeskFaderConstants.SlotCount).Select(number => new SlotRow(number)));
        DataContext = this;
        InitializeComponent();
        Content = BuildDashboard();
        service.StateChanged += ServiceStateChanged;
        Update(service.CurrentState(), reloadMappings: true);
        Closed += (_, _) => service.StateChanged -= ServiceStateChanged;
    }

    public ObservableCollection<FaderChannel> Channels { get; }
    public ObservableCollection<SlotRow> Slots { get; }
    public ObservableCollection<string> ActiveApps { get; } = [];
    public string Status { get => status; private set { status = value; OnPropertyChanged(); } }
    public bool StartAtLogin { get => startAtLogin; set { startAtLogin = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Settings_Click(object sender, RoutedEventArgs e) => settingsDrawer!.Visibility = settingsDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void ServiceStateChanged(ServiceState state)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Update(state, reloadMappings: false)); return; }
        Update(state, reloadMappings: false);
    }

    private void Update(ServiceState state, bool reloadMappings)
    {
        if (!ActiveApps.SequenceEqual(state.ActiveApps, StringComparer.OrdinalIgnoreCase))
        {
            ActiveApps.Clear();
            foreach (var app in state.ActiveApps) ActiveApps.Add(app);
        }
        for (var index = 0; index < Channels.Count; index++)
        {
            var slot = index < state.Slots.Count ? state.Slots[index] : null;
            Channels[index].Mapping = slot?.Process ?? "Unmapped";
            Channels[index].Target = index < state.Volumes.Count ? state.Volumes[index] : 0;
            Channels[index].Selected = state.SelectedSlot == index;
            if (reloadMappings) Slots[index].Process = slot?.Process;
            Slots[index].Target = index < state.Volumes.Count ? $"Target: {state.Volumes[index]}%" : "Target: --";
        }
        if (reloadMappings) StartAtLogin = TryGetStartAtLogin();
        Status = state.Error ?? (state.Running ? "Service running" : "Service stopped");
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Update(service.CurrentState(), reloadMappings: false);
    private void ReloadSaved_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Discard unsaved mapping changes and reload the saved mappings?", "DeskFader", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            Update(service.CurrentState(), reloadMappings: true);
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SlotRow row) return;
        var dialog = new OpenFileDialog { Title = "Choose application executable", Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) row.Process = Path.GetFileName(dialog.FileName);
    }
    private void Clear_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is SlotRow row) row.Process = null; }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var slots = SlotValidator.Validate(Slots.Select((row, index) => new Slot { Process = string.IsNullOrWhiteSpace(row.Process) ? null : row.Process, DefaultVolume = service.CurrentState().Volumes.ElementAtOrDefault(index) }));
            StartupRegistrar.SetStartAtLogin(StartAtLogin);
            service.ApplyConfiguration(slots, StartAtLogin);
            Status = "Mapping applied.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "DeskFader", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private FrameworkElement BuildDashboard()
    {
        Background = new SolidColorBrush(Color.FromRgb(20, 24, 31)); Foreground = Brushes.White; Width = 1040; Height = 560; MinWidth = 740; MinHeight = 440;
        var root = new Grid { Margin = new Thickness(20) }; root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dashboard = new Grid(); dashboard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); dashboard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); dashboard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        var settings = new Button { Content = "Settings", Padding = new Thickness(14, 6, 14, 6) }; settings.Click += Settings_Click; DockPanel.SetDock(settings, Dock.Right); header.Children.Add(settings); header.Children.Add(new TextBlock { Text = "DESKFADER", FontSize = 20, FontWeight = FontWeights.Bold }); dashboard.Children.Add(header);
        var channels = new Grid { Margin = new Thickness(0, 0, 0, 12) }; for (var index = 0; index < DeskFaderConstants.SlotCount; index++) { channels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); var channel = BuildChannel(Channels[index]); Grid.SetColumn(channel, index); channels.Children.Add(channel); } Grid.SetRow(channels, 1); dashboard.Children.Add(channels);
        var statusPanel = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(190, 201, 216)), TextTrimming = TextTrimming.CharacterEllipsis }; statusPanel.SetBinding(TextBlock.TextProperty, new Binding(nameof(Status))); Grid.SetRow(statusPanel, 2); dashboard.Children.Add(statusPanel); root.Children.Add(dashboard);
        settingsDrawer = BuildSettingsDrawer(); Grid.SetColumn(settingsDrawer, 1); root.Children.Add(settingsDrawer); return root;
    }

    private Border BuildSettingsDrawer()
    {
        var drawer = new Border { Visibility = Visibility.Collapsed, Width = 410, Margin = new Thickness(20, 0, 0, 0), Padding = new Thickness(14), Background = new SolidColorBrush(Color.FromRgb(31, 37, 47)), CornerRadius = new CornerRadius(5) };
        var layout = new DockPanel(); var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (text, handler) in new[] { ("Apply", new RoutedEventHandler(Apply_Click)), ("Refresh", new RoutedEventHandler(Refresh_Click)), ("Reload saved", new RoutedEventHandler(ReloadSaved_Click)) }) { var button = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0) }; button.Click += handler; actions.Children.Add(button); }
        DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions); var startup = new CheckBox { Content = "Start at login", Margin = new Thickness(0, 8, 0, 8) }; startup.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(StartAtLogin)) { Mode = BindingMode.TwoWay }); DockPanel.SetDock(startup, Dock.Bottom); layout.Children.Add(startup);
        var rows = new StackPanel(); rows.Children.Add(new TextBlock { Text = "Mappings", FontSize = 16, FontWeight = FontWeights.Bold }); foreach (var row in Slots) rows.Children.Add(BuildSlotRow(row)); var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; layout.Children.Add(scroll); drawer.Child = layout; return drawer;
    }

    private FrameworkElement BuildSlotRow(SlotRow row)
    {
        var grid = new Grid { DataContext = row, Margin = new Thickness(0, 5, 0, 0) }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center }; label.SetBinding(TextBlock.TextProperty, new Binding(nameof(SlotRow.Label))); grid.Children.Add(label);
        var process = new ComboBox { IsEditable = true, ItemsSource = ActiveApps };
        process.SetBinding(ComboBox.TextProperty, new Binding(nameof(SlotRow.Process)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        process.SelectionChanged += (_, _) =>
        {
            if (process.SelectedItem is string selected) row.Process = selected;
        };
        Grid.SetColumn(process, 1); grid.Children.Add(process);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; var browse = new Button { Content = "Browse", Tag = row, Margin = new Thickness(5, 0, 0, 0) }; browse.Click += Browse_Click; actions.Children.Add(browse); var clear = new Button { Content = "Clear", Tag = row, Margin = new Thickness(4, 0, 0, 0) }; clear.Click += Clear_Click; actions.Children.Add(clear); Grid.SetColumn(actions, 2); grid.Children.Add(actions); var target = new TextBlock { Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; target.SetBinding(TextBlock.TextProperty, new Binding(nameof(SlotRow.Target))); Grid.SetColumn(target, 3); grid.Children.Add(target); return grid;
    }

    private static FrameworkElement BuildChannel(FaderChannel channel)
    {
        var border = new Border { DataContext = channel, Background = new SolidColorBrush(Color.FromRgb(31, 37, 47)), BorderBrush = new SolidColorBrush(Color.FromRgb(54, 64, 80)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Margin = new Thickness(4), Padding = new Thickness(8) };
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(FaderChannel.Selected)) { Converter = new SelectionFillConverter() });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(FaderChannel.Selected)) { Converter = new SelectionBrushConverter() });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(FaderChannel.Selected)) { Converter = new SelectionThicknessConverter() });
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold }; label.SetBinding(TextBlock.TextProperty, new Binding(nameof(FaderChannel.Label))); layout.Children.Add(label); var mapping = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 110 }; mapping.SetBinding(TextBlock.TextProperty, new Binding(nameof(FaderChannel.Mapping))); Grid.SetRow(mapping, 1); layout.Children.Add(mapping); var slider = new Slider { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100, IsHitTestVisible = false, Focusable = false, Width = 34, Margin = new Thickness(0, 12, 0, 8) }; slider.SetBinding(Slider.ValueProperty, new Binding(nameof(FaderChannel.Target))); slider.SetBinding(Control.ForegroundProperty, new Binding(nameof(FaderChannel.Selected)) { Converter = new SelectionBrushConverter() }); Grid.SetRow(slider, 2); layout.Children.Add(slider); var target = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold }; target.SetBinding(TextBlock.TextProperty, new Binding(nameof(FaderChannel.TargetLabel))); target.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(FaderChannel.Selected)) { Converter = new SelectionTextBrushConverter() }); Grid.SetRow(target, 3); layout.Children.Add(target); border.Child = layout; return border;
    }

    private bool TryGetStartAtLogin() { try { return new SettingsStore().Load().StartAtLogin; } catch { return false; } }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public sealed class FaderChannel(int number) : INotifyPropertyChanged { private string mapping = "Unmapped"; private int target; private bool selected; public string Label => $"SLOT {number:00}"; public string Mapping { get => mapping; set { mapping = value; OnPropertyChanged(); } } public int Target { get => target; set { target = value; OnPropertyChanged(); OnPropertyChanged(nameof(TargetLabel)); } } public bool Selected { get => selected; set { selected = value; OnPropertyChanged(); } } public string TargetLabel => $"Target {Target}%"; public event PropertyChangedEventHandler? PropertyChanged; private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
    public sealed class SlotRow(int number) : INotifyPropertyChanged { private string? process; private string target = "Target: --"; public string Label => $"Slot {number}"; public string? Process { get => process; set { process = value; OnPropertyChanged(); } } public string Target { get => target; set { target = value; OnPropertyChanged(); } } public event PropertyChangedEventHandler? PropertyChanged; private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
    private sealed class SelectionBrushConverter : IValueConverter { public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? Brushes.DeepSkyBlue : new SolidColorBrush(Color.FromRgb(54, 64, 80)); public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException(); }
    private sealed class SelectionFillConverter : IValueConverter { public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? new SolidColorBrush(Color.FromRgb(20, 75, 98)) : new SolidColorBrush(Color.FromRgb(31, 37, 47)); public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException(); }
    private sealed class SelectionThicknessConverter : IValueConverter { public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? new Thickness(3) : new Thickness(1); public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException(); }
    private sealed class SelectionTextBrushConverter : IValueConverter { public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? Brushes.DeepSkyBlue : Brushes.White; public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException(); }
}
