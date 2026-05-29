using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace FH6SQLiteEditorNative;

public partial class MainWindow : Window
{
    private sealed record LinkTarget(string Table, string Column, object Value);

    private readonly AppSettings _settings = AppSettings.Load();
    private SqliteEditorService? _editor;
    private DataTable? _currentTable;
    private string? _currentTableName;
    private CarListItem? _selectedCar;
    private CancellationTokenSource? _taskCts;
    private bool _loadingTableChoices;
    private bool _loadingEngineTools;
    private bool _loadingPartTools;
    private bool _suppressTableSelectionChanged;
    private bool _darkMode;
    private bool _isClosing;
    private string _tableSearchText = "";
    private readonly HashSet<string> _forceReplaceOnNextImport = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppPaths.CleanOldSessions();
        _darkMode = _settings.DarkMode;
        ApplyTheme();

        var baseDb = AppPaths.FindBaseDb();
        var initialDb = !string.IsNullOrWhiteSpace(baseDb) && File.Exists(baseDb) ? baseDb :
            File.Exists(AppPaths.DefaultDbPath) ? AppPaths.DefaultDbPath :
            File.Exists(_settings.LastDbPath) ? _settings.LastDbPath :
            null;

        if (!string.IsNullOrWhiteSpace(initialDb) && File.Exists(initialDb))
        {
            OpenDatabase(initialDb);
        }
        else
        {
            AppendLog("Open a FH6 SQLite dump to begin.");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        var hadRunningTask = _taskCts is not null;
        _taskCts?.Cancel();
        _editor?.Dispose();
        _editor = null;
        _settings.DarkMode = _darkMode;
        _settings.Save();
        AppPaths.CleanOldSessions();

        if (hadRunningTask)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                Environment.Exit(0);
            });
        }
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _darkMode = !_darkMode;
        _settings.DarkMode = _darkMode;
        _settings.Save();
        ApplyTheme();
    }

    private void OpenDbButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open FH6 SQLite DB",
            Filter = "SQLite databases (*.sqlite;*.db;*.slt)|*.sqlite;*.db;*.slt|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(_settings.LastExportDir) ? _settings.LastExportDir : AppPaths.PackageRoot
        };

        if (dialog.ShowDialog(this) == true)
        {
            OpenDatabase(dialog.FileName);
        }
    }

    private async void DumpButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureIdle())
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save FH6 SQLite dump",
            Filter = "SQLite databases (*.sqlite)|*.sqlite|SQLite DB (*.db)|*.db|All files (*.*)|*.*",
            FileName = "fh6_db.sqlite",
            InitialDirectory = Directory.Exists(_settings.LastExportDir)
                ? _settings.LastExportDir
                : Path.Combine(AppPaths.PackageRoot, "Export")
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _settings.LastExportDir = Path.GetDirectoryName(dialog.FileName);
        _settings.Save();

        var dumper = new GameDatabaseDumper();
        await RunToolAsync(
            "Dumping from game...",
            token => dumper.DumpAsync(dialog.FileName, new Progress<string>(AppendLog), token));

        if (File.Exists(dialog.FileName))
        {
            OpenDatabase(dialog.FileName);
        }
    }

    private void SaveAsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null)
        {
            MessageBox.Show(this, "Open a database first.", "FH6 SQLite Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryApplyCurrentTableChanges(showSuccess: false))
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Save edited FH6 SQLite DB",
            Filter = "SQLite databases (*.sqlite)|*.sqlite|SQLite DB (*.db)|*.db|All files (*.*)|*.*",
            FileName = "fh6_db.edited.sqlite",
            InitialDirectory = Directory.Exists(_settings.LastExportDir)
                ? _settings.LastExportDir
                : Path.Combine(AppPaths.PackageRoot, "Export")
        };

        if (dialog.ShowDialog(this) == true)
        {
            _editor.SaveAs(dialog.FileName);
            _settings.LastExportDir = Path.GetDirectoryName(dialog.FileName);
            _settings.Save();
            AppendLog($"Saved: {dialog.FileName}");
            TableStatusText.Text = "Saved edited DB.";
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureIdle() || _editor is null)
        {
            return;
        }

        if (!TryApplyCurrentTableChanges(showSuccess: false))
        {
            return;
        }
        _editor.FlushForImport();

        var baseDb = AppPaths.FindBaseDb();
        if (string.IsNullOrWhiteSpace(baseDb) || !File.Exists(baseDb))
        {
            MessageBox.Show(
                this,
                "Put a clean untouched dump in the BASE DB folder first. Preferred name: fh6_db.sqlite.",
                "Missing BASE DB",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var importer = new GameDatabaseImporter();
        IReadOnlyList<LocalTable> changed;
        var forcedTables = _forceReplaceOnNextImport.ToList();
        try
        {
            changed = importer.PreviewChangedTables(_editor.SessionPath, baseDb, importAll: false, forcedTables);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not diff DB", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (changed.Count == 0)
        {
            AppendLog("No changed tables found. Nothing to import.");
            return;
        }

        var preview = string.Join(Environment.NewLine, changed.Take(12).Select(t => $"  {t.Name} ({t.RowCount} rows)"));
        if (changed.Count > 12)
        {
            preview += Environment.NewLine + $"  ...and {changed.Count - 12} more";
        }

        var confirm = MessageBox.Show(
            this,
            $"This will patch {changed.Count} changed table(s) into the running game process:{Environment.NewLine}{preview}{Environment.NewLine}{Environment.NewLine}Continue?",
            "Import to Game",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var imported = await RunToolAsync(
            "Importing to game...",
            token => importer.ImportAsync(_editor.SessionPath, baseDb, importAll: false, new Progress<string>(AppendLog), token, forcedTables));
        if (imported)
        {
            foreach (var table in forcedTables)
            {
                _forceReplaceOnNextImport.Remove(table);
            }
        }
    }

    private async void ResetGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureIdle())
        {
            return;
        }
        if (_editor is not null)
        {
            if (!TryApplyCurrentTableChanges(showSuccess: false))
            {
                return;
            }
            _editor.FlushForImport();
        }

        var baseDb = AppPaths.FindBaseDb();
        if (string.IsNullOrWhiteSpace(baseDb) || !File.Exists(baseDb))
        {
            MessageBox.Show(
                this,
                "Put a clean untouched dump in the BASE DB folder first. Preferred name: fh6_db.sqlite.",
                "Missing BASE DB",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "This resets the running game DB back to BASE DB. It first tries a fast in-game compare; if the game cannot attach the BASE DB file, it streams BASE DB rows from this app instead. It does not change your open editor session or saved files." +
            Environment.NewLine + Environment.NewLine +
            "Continue?",
            "Reset Game to BASE DB",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var importer = new GameDatabaseImporter();
        await RunToolAsync(
            "Resetting live game DB to BASE DB...",
            token => importer.ResetGameToBaseAsync(baseDb, _editor?.SessionPath, new Progress<string>(AppendLog), token));
    }

    private void ValidateButton_Click(object sender, RoutedEventArgs e)
    {
        RunValidation(showDialog: true);
    }

    private void RunValidateTabButton_Click(object sender, RoutedEventArgs e) => RunValidation(showDialog: false);

    private void CancelTaskButton_Click(object sender, RoutedEventArgs e)
    {
        _taskCts?.Cancel();
        AppendLog("Cancellation requested.");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshCarList();

    private void VisibilityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCarList();

    private void CarListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedCar = CarListBox.SelectedItem as CarListItem;
        if (_selectedCar is null)
        {
            CarTitleText.Text = "Select a car";
            CarSubtitleText.Text = "Or open any table from the tabs below.";
        }
        else
        {
            CarTitleText.Text = _selectedCar.Title;
            CarSubtitleText.Text = _selectedCar.Subtitle;
        }

        LoadSelectedTable();
        UpdateTemplatePanel();
        RefreshEngineTools();
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == EditorTabs)
        {
            PopulateTableChoices();
        }
    }

    private void TableCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTableChoices || _suppressTableSelectionChanged)
        {
            return;
        }

        if (!TryApplyCurrentTableChanges(showSuccess: false))
        {
            return;
        }
        LoadSelectedTable();
    }

    private void TableSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _tableSearchText = TableSearchBox.Text.Trim();
        TableGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
        RenderCurrentTableView();
        UpdateTableStatus();
    }

    private void RefreshTableButton_Click(object sender, RoutedEventArgs e) => LoadSelectedTable();

    private void ApplyChangesButton_Click(object sender, RoutedEventArgs e) => TryApplyCurrentTableChanges(showSuccess: true);

    private void TableGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName == "__fh6_rowid")
        {
            e.Cancel = true;
            return;
        }

        e.Column.Header = HeaderTextForColumn(_currentTableName, e.PropertyName);
        e.Column.MinWidth = 90;
        e.Column.MaxWidth = 360;
        if (_editor is not null &&
            _currentTableName is not null &&
            !_editor.WritableColumnNames(_currentTableName).Contains(e.PropertyName))
        {
            e.Column.IsReadOnly = true;
        }
    }

    private void AddTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null || _currentTableName is null)
        {
            return;
        }
        if (!long.TryParse(TemplateEngineIdBox.Text, out var engineId))
        {
            MessageBox.Show(this, "Enter the target EngineID first.", "Add Engine Part", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (TemplateCombo.SelectedItem is not ValueTuple<string, long, string> selected)
        {
            MessageBox.Show(this, "Choose a template row first.", "Add Engine Part", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _editor.AddEnginePartFromTemplate(_currentTableName, engineId, selected.Item1, selected.Item2);
            AppendLog($"Added {_currentTableName} row for EngineID {engineId} from {selected.Item1} Id {selected.Item2}");
            LoadSelectedTable();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Add Engine Part", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadEngineSwapsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCar is null)
        {
            MessageBox.Show(this, "Select a car first.", "Engine Swaps", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectTableChoice("List_UpgradeEngine");
        LoadSelectedTable();
    }

    private void LoadBaseEngineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }
        if (!TryGetCurrentEngineId(out var engineId))
        {
            MessageBox.Show(this, "Select one of the current car engines first.", "Base Engine Stats", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        TryApplyCurrentTableChanges(showSuccess: false);
        SelectTableChoice("Data_Engine");
        _currentTableName = "Data_Engine";
        _currentTable = _editor.LoadEngineBase(engineId);
        RenderCurrentTableView();
        UpdateTableStatus($"EngineID {engineId}");
        AppendLog($"Loaded base engine stats for EngineID {engineId}");
    }

    private void EngineSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingEngineTools)
        {
            RefreshEngineCatalog();
        }
    }

    private void AddEngineSwapButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null || _selectedCar is null)
        {
            MessageBox.Show(this, "Select a car first.", "Add Engine Swap", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (EngineCatalogCombo.SelectedItem is not EngineChoice engine)
        {
            MessageBox.Show(this, "Choose an engine from the catalog first.", "Add Engine Swap", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            TryApplyCurrentTableChanges(showSuccess: false);
            var created = _editor.AddEngineSwap(_selectedCar.Id, engine.EngineId);
            AppendLog(created
                ? $"Added engine swap {engine.EngineId} to {_selectedCar.Title}"
                : $"Engine swap {engine.EngineId} already exists for {_selectedCar.Title}");
            RefreshEngineTools();
            SelectTableChoice("List_UpgradeEngine");
            LoadSelectedTable();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Add Engine Swap failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PartEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingPartTools)
        {
            RefreshPartTemplates();
            LoadSelectedPartRows();
        }
    }

    private void PartTableCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingPartTools)
        {
            RefreshPartTemplates();
            LoadSelectedPartRows();
        }
    }

    private void LoadPartRowsButton_Click(object sender, RoutedEventArgs e) => LoadSelectedPartRows();

    private void AddPartOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }
        if (PartEngineCombo.SelectedItem is not EngineChoice engine ||
            PartTableCombo.SelectedItem is not TableChoice table)
        {
            MessageBox.Show(this, "Pick an engine, part table, and template first.", "Add Engine Part", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var template = SelectedPartTemplate();
        if (template is null)
        {
            MessageBox.Show(this, "No template rows exist for this part type yet.", "Add Engine Part", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            TryApplyCurrentTableChanges(showSuccess: false);
            _editor.AddEnginePartFromTemplate(table.Name, engine.EngineId, template.SourceTable, template.SourceId);
            AppendLog($"Added {table.Name} row for EngineID {engine.EngineId} from {template.SourceTable} Id {template.SourceId}");
            RefreshPartTemplates();
            LoadSelectedPartRows();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Add Engine Part failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddAspirationConversionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }
        if (PartEngineCombo.SelectedItem is not EngineChoice engine ||
            AspirationConversionCombo.SelectedItem is not AspirationConversionChoice conversion)
        {
            MessageBox.Show(this, "Pick an engine and aspiration conversion first.", "Add Aspiration Conversion", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            TryApplyCurrentTableChanges(showSuccess: false);
            var message = _editor.AddAspirationConversion(engine.EngineId, conversion.AspirationId);
            AppendLog(message);
            SyncPartTableToolChoice(conversion.TableName);
            RefreshPartTools();
            SyncPartTableToolChoice(conversion.TableName);
            LoadSelectedPartRows();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Add Aspiration Conversion failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveAspirationConversionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }
        if (PartEngineCombo.SelectedItem is not EngineChoice engine ||
            AspirationConversionCombo.SelectedItem is not AspirationConversionChoice conversion)
        {
            MessageBox.Show(this, "Pick an engine and aspiration conversion first.", "Remove Aspiration Conversion", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Remove all {conversion.PartName} rows for EngineID {engine.EngineId} from this editor session? This is useful for testing, but the running game may need Reset Game to Base if that conversion is currently installed.",
            "Remove Aspiration Conversion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            TryApplyCurrentTableChanges(showSuccess: false);
            var message = _editor.RemoveAspirationConversion(engine.EngineId, conversion.AspirationId);
            ForceLiveReplace(conversion.TableName);
            if (conversion.PartName.Equals("QuadTurbo", StringComparison.OrdinalIgnoreCase))
            {
                ForceLiveReplace("Upgrades");
            }
            AppendLog(message);
            SyncPartTableToolChoice(conversion.TableName);
            RefreshPartTools();
            SyncPartTableToolChoice(conversion.TableName);
            LoadSelectedPartRows();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Remove Aspiration Conversion failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EnableAspirationTypeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null ||
            PartTableCombo.SelectedItem is not TableChoice table ||
            !_editor.CanWireMenuMetadata(table.Name))
        {
            MessageBox.Show(this, "Pick a mapped upgrade part table first.", "Wire Menu", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            TryApplyCurrentTableChanges(showSuccess: false);
            var message = _editor.EnsureMenuMetadataForTable(table.Name);
            AppendLog(message);
            RefreshPartTemplates();
            LoadSelectedPartRows();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Wire Menu failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ProbeMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null ||
            PartEngineCombo.SelectedItem is not EngineChoice engine ||
            PartTableCombo.SelectedItem is not TableChoice table)
        {
            MessageBox.Show(this, "Pick an engine and engine part table first.", "Probe Menu", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            TryApplyCurrentTableChanges(showSuccess: false);
            var report = _editor.ProbeUpgradeMenuChain(table.Name, engine.EngineId);
            RenderMenuProbeView(report);
            AppendLog($"Probed menu chain for {table.Name} EngineID {engine.EngineId}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Probe Menu failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ProbeAllMenusButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null ||
            PartEngineCombo.SelectedItem is not EngineChoice engine)
        {
            MessageBox.Show(this, "Pick one of the current car engines first.", "Probe All", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            TryApplyCurrentTableChanges(showSuccess: false);
            var report = _editor.ProbeUpgradeMenuOverview(engine.EngineId);
            RenderMenuProbeView(report);
            AppendLog($"Probed all engine part menu chains for EngineID {engine.EngineId}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Probe All failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WireMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null || _currentTableName is null || !_editor.CanWireMenuMetadata(_currentTableName))
        {
            MessageBox.Show(this, "Open a mapped upgrade table first.", "Wire Menu", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            TryApplyCurrentTableChanges(showSuccess: false);
            AppendLog(_editor.EnsureMenuMetadataForTable(_currentTableName));
            LoadSelectedTable();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Wire Menu failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddLinkedOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null || _selectedCar is null || _currentTableName is null)
        {
            MessageBox.Show(this, "Select a car and an upgrade/aero table first.", "Add Option", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            TryApplyCurrentTableChanges(showSuccess: false);
            if (EditorConstants.UpgradeTableLinks.ContainsKey(_currentTableName))
            {
                _editor.AddLinkedUpgradeOption(_currentTableName, _selectedCar.Id);
            }
            else if (EditorConstants.AeroOptionTables.Contains(_currentTableName, StringComparer.OrdinalIgnoreCase))
            {
                _editor.AddLinkedAeroOption(_currentTableName, _selectedCar.Id);
            }
            else
            {
                MessageBox.Show(this, "This table does not support Add Option.", "Add Option", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AppendLog($"Added option to {_currentTableName} for {_selectedCar.Title}");
            LoadSelectedTable();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Add Option failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenDatabase(string path)
    {
        try
        {
            _editor?.Dispose();
            _forceReplaceOnNextImport.Clear();
            _editor = new SqliteEditorService(path);
            _settings.LastDbPath = path;
            _settings.LastExportDir = Path.GetDirectoryName(path);
            _settings.Save();

            StatusText.Text = path;
            AppendLog($"Opened: {path}");
            RefreshCarList();
            PopulateTableChoices();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open DB failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshCarList()
    {
        if (_editor is null || !IsLoaded)
        {
            return;
        }

        try
        {
            var filter = ((VisibilityFilter.SelectedItem as ComboBoxItem)?.Tag as string) ?? "all";
            var selectedId = _selectedCar?.Id;
            var cars = _editor.SearchCars(SearchBox.Text, filter);
            CarListBox.ItemsSource = cars;
            if (selectedId.HasValue)
            {
                CarListBox.SelectedItem = cars.FirstOrDefault(c => c.Id == selectedId.Value);
            }
        }
        catch (Exception ex)
        {
            AppendLog("Car search failed: " + ex.Message);
        }
    }

    private void PopulateTableChoices()
    {
        if (_editor is null)
        {
            return;
        }

        TryApplyCurrentTableChanges(showSuccess: false);
        _loadingTableChoices = true;
        try
        {
            var tables = SelectedTabIndex() switch
            {
                0 => _editor.ExistingTables(EditorConstants.CoreTables),
                1 => _editor.ExistingTables(EditorConstants.EngineTables),
                2 => _editor.ExistingTables(EditorConstants.EnginePartTables),
                3 => _editor.ExistingTables(EditorConstants.DefaultTables),
                4 => _editor.ExistingTables(EditorConstants.UpgradeTables),
                5 => _editor.ExistingTables(EditorConstants.AeroTables),
                6 => _editor.ExistingTables(EditorConstants.LookupTables),
                7 => _editor.ExistingTables(EditorConstants.TuningLimitTables),
                8 => Array.Empty<string>(),
                _ => _editor.AllTables()
            };

            TableCombo.ItemsSource = tables.Select(t => new TableChoice(t)).ToList();
            TableCombo.SelectedIndex = TableCombo.Items.Count > 0 ? 0 : -1;
        }
        finally
        {
            _loadingTableChoices = false;
        }

        UpdateTabToolPanels();
        if (IsValidateTab())
        {
            RenderValidateView();
        }
        else
        {
            LoadSelectedTable();
        }
    }

    private void LoadSelectedTable()
    {
        if (_editor is null || TableCombo.SelectedItem is not TableChoice choice)
        {
            TableGrid.ItemsSource = null;
            _currentTable = null;
            _currentTableName = null;
            return;
        }

        try
        {
            _currentTableName = choice.Name;
            if (_selectedCar is null && IsCarScopedTab())
            {
                _currentTable = null;
                ShowTableMessage("Select a car on the left to load car-specific rows. Use Lookups or All Tables for global DB browsing.");
                UpdateTemplatePanel();
                UpdateTabToolPanels();
                return;
            }

            if (choice.Name == "List_UpgradeEngine" && _selectedCar is not null)
            {
                _currentTable = _editor.LoadEngineSwaps(_selectedCar.Id);
            }
            else if (SelectedTabIndex() == 2 &&
                     EditorConstants.EnginePartTables.Contains(choice.Name, StringComparer.OrdinalIgnoreCase) &&
                     TryGetSelectedPartEngineId(out var partEngineId))
            {
                SyncPartTableToolChoice(choice.Name);
                _currentTable = _editor.LoadEngineParts(choice.Name, partEngineId);
            }
            else if (choice.Name == "Data_Engine" && SelectedTabIndex() == 1 && TryGetCurrentEngineId(out var engineId))
            {
                _currentTable = _editor.LoadEngineBase(engineId);
            }
            else
            {
                _currentTable = _editor.LoadTable(choice.Name, _selectedCar?.Id);
            }
            RenderCurrentTableView();
            var status = choice.Name == "Data_Engine" && SelectedTabIndex() == 1 && _currentTable.Rows.Count == 1
                ? "selected engine row loaded"
                : SelectedTabIndex() == 2 && TryGetSelectedPartEngineId(out var loadedPartEngineId)
                    ? $"EngineID {loadedPartEngineId}"
                    : null;
            UpdateTableStatus(status);
            UpdateTemplatePanel();
            UpdateTabToolPanels();
        }
        catch (Exception ex)
        {
            TableGrid.ItemsSource = null;
            _currentTable = null;
            AppendLog($"Load table failed: {ex.Message}");
            TableStatusText.Text = "Table load failed.";
        }
    }

    private void ShowTableMessage(string message)
    {
        TableGrid.ItemsSource = null;
        TableGrid.Visibility = Visibility.Collapsed;
        HtmlTableBorder.Visibility = Visibility.Collapsed;
        ValidatePanel.Visibility = Visibility.Collapsed;
        MenuProbePanel.Visibility = Visibility.Collapsed;
        HtmlTableGrid.Children.Clear();
        FieldFormPanel.Children.Clear();
        FieldFormBorder.Visibility = Visibility.Visible;

        var text = new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4),
            MaxWidth = 720,
            Foreground = Brush(_darkMode ? "#98a9ae" : "#64747b")
        };
        FieldFormPanel.Children.Add(text);
        TableStatusText.Text = "Waiting for car selection.";
    }

    private void RenderCurrentTableView()
    {
        if (_currentTable is null)
        {
            TableGrid.ItemsSource = null;
            TableGrid.Visibility = Visibility.Visible;
            FieldFormBorder.Visibility = Visibility.Collapsed;
            HtmlTableBorder.Visibility = Visibility.Collapsed;
            ValidatePanel.Visibility = Visibility.Collapsed;
            MenuProbePanel.Visibility = Visibility.Collapsed;
            HtmlTableGrid.Children.Clear();
            FieldFormPanel.Children.Clear();
            return;
        }

        if (_currentTable.Rows.Count == 1)
        {
            TableGrid.ItemsSource = null;
            TableGrid.Visibility = Visibility.Collapsed;
            HtmlTableBorder.Visibility = Visibility.Collapsed;
            ValidatePanel.Visibility = Visibility.Collapsed;
            MenuProbePanel.Visibility = Visibility.Collapsed;
            HtmlTableGrid.Children.Clear();
            FieldFormBorder.Visibility = Visibility.Visible;
            RenderFieldForm(_currentTable.Rows[0]);
            return;
        }

        FieldFormPanel.Children.Clear();
        FieldFormBorder.Visibility = Visibility.Collapsed;
        ValidatePanel.Visibility = Visibility.Collapsed;
        MenuProbePanel.Visibility = Visibility.Collapsed;
        var displayRows = CurrentDisplayRows();
        if (ShouldUseHtmlTable(displayRows.Count))
        {
            TableGrid.ItemsSource = null;
            TableGrid.Visibility = Visibility.Collapsed;
            HtmlTableBorder.Visibility = Visibility.Visible;
            RenderHtmlEditableTable(displayRows);
            return;
        }

        HtmlTableBorder.Visibility = Visibility.Collapsed;
        HtmlTableGrid.Children.Clear();
        TableGrid.Visibility = Visibility.Visible;
        ApplyDataGridFilter();
    }

    private bool ShouldUseHtmlTable(int displayRowCount)
    {
        return _currentTable is not null &&
               _currentTableName is not null &&
               displayRowCount is > 1 and <= 250 &&
               SelectedTabIndex() is >= 1 and <= 7;
    }

    private void RenderHtmlEditableTable(IReadOnlyList<DataRow> rows)
    {
        if (_editor is null || _currentTable is null || _currentTableName is null)
        {
            return;
        }

        HtmlTableGrid.Children.Clear();
        HtmlTableGrid.RowDefinitions.Clear();
        HtmlTableGrid.ColumnDefinitions.Clear();

        var columns = VisibleColumnsForTable(_currentTableName, _currentTable).ToList();
        var writable = _editor.WritableColumnNames(_currentTableName);
        foreach (var column in columns)
        {
            HtmlTableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidth(column)) });
        }
        HtmlTableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        HtmlTableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var c = 0; c < columns.Count; c++)
        {
            AddHtmlHeaderCell(HumanizeFieldName(columns[c]), c, columns[c]);
        }
        AddHtmlHeaderCell("Actions", columns.Count);

        for (var r = 0; r < rows.Count; r++)
        {
            HtmlTableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = rows[r];
            for (var c = 0; c < columns.Count; c++)
            {
                AddHtmlDataCell(row, columns[c], writable.Contains(columns[c]), r + 1, c);
            }
            AddHtmlActionCell(row, r + 1, columns.Count);
        }
    }

    private List<DataRow> CurrentDisplayRows()
    {
        if (_currentTable is null)
        {
            return [];
        }

        var rows = _currentTable.Rows.Cast<DataRow>()
            .Where(row => row.RowState != DataRowState.Deleted);
        if (string.IsNullOrWhiteSpace(_tableSearchText))
        {
            return rows.ToList();
        }

        return rows.Where(RowMatchesTableSearch).ToList();
    }

    private bool RowMatchesTableSearch(DataRow row)
    {
        var search = NormalizeSearchValue(_tableSearchText);
        foreach (DataColumn column in row.Table.Columns)
        {
            if (column.ColumnName == "__fh6_rowid" || row[column] == DBNull.Value)
            {
                continue;
            }

            var value = Convert.ToString(row[column]) ?? "";
            if (value.Contains(_tableSearchText, StringComparison.OrdinalIgnoreCase) ||
                NormalizeSearchValue(value).Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void ApplyDataGridFilter()
    {
        if (_currentTable is null)
        {
            TableGrid.ItemsSource = null;
            return;
        }

        try
        {
            _currentTable.DefaultView.RowFilter = BuildDataViewFilter(_currentTable, _tableSearchText);
        }
        catch
        {
            _currentTable.DefaultView.RowFilter = "";
        }
        TableGrid.ItemsSource = _currentTable.DefaultView;
    }

    private void UpdateTableStatus(string? specialStatus = null)
    {
        if (_currentTable is null || string.IsNullOrWhiteSpace(_currentTableName))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(specialStatus))
        {
            TableStatusText.Text = $"{_currentTableName}: {specialStatus}";
            return;
        }

        var total = _currentTable.Rows.Cast<DataRow>().Count(row => row.RowState != DataRowState.Deleted);
        var visible = string.IsNullOrWhiteSpace(_tableSearchText)
            ? total
            : CurrentDisplayRows().Count;
        TableStatusText.Text = visible == total
            ? $"{_currentTableName}: {total} loaded row(s)"
            : $"{_currentTableName}: {visible} of {total} row(s) visible";
    }

    private static string BuildDataViewFilter(DataTable table, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return "";
        }

        var variants = new[]
            {
                search.Trim(),
                NormalizeSearchValue(search),
                search.Trim().Replace(' ', '_'),
                search.Trim().Replace(' ', '-')
            }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(EscapeDataViewLikeLiteral)
            .ToList();
        var parts = new List<string>();
        foreach (DataColumn column in table.Columns)
        {
            if (column.ColumnName == "__fh6_rowid")
            {
                continue;
            }

            var name = EscapeDataViewColumnName(column.ColumnName);
            foreach (var variant in variants)
            {
                parts.Add($"Convert([{name}], 'System.String') LIKE '%{variant}%'");
            }
        }

        return parts.Count == 0 ? "" : string.Join(" OR ", parts);
    }

    private static string NormalizeSearchValue(string value)
    {
        return value
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();
    }

    private static string EscapeDataViewColumnName(string columnName)
    {
        return columnName.Replace("\\", "\\\\").Replace("]", "\\]");
    }

    private static string EscapeDataViewLikeLiteral(string value)
    {
        return value
            .Replace("'", "''")
            .Replace("[", "[[]")
            .Replace("%", "[%]")
            .Replace("*", "[*]");
    }

    private void AddHtmlHeaderCell(string textValue, int column, string? columnName = null)
    {
        var role = columnName is null ? null : FieldValueRoles.ForColumn(_currentTableName, columnName);
        var label = new TextBlock
        {
            Text = textValue,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(_darkMode ? "#b6c8cd" : "#64747b"),
            TextWrapping = TextWrapping.Wrap
        };
        FrameworkElement child = label;
        if (role is { ShowBadge: true })
        {
            child = new StackPanel
            {
                Children =
                {
                    label,
                    new Border
                    {
                        Background = RoleBadgeBackground(role.Kind),
                        BorderBrush = RoleBadgeBorder(role.Kind),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(5, 1, 5, 1),
                        Margin = new Thickness(0, 5, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new TextBlock
                        {
                            Text = role.Label,
                            FontSize = 10,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = RoleBadgeForeground(role.Kind)
                        }
                    }
                }
            };
        }

        var border = new Border
        {
            Background = Brush(_darkMode ? "#1b2b30" : "#f4f7f8"),
            BorderBrush = Brush(_darkMode ? "#2d4047" : "#d6e0e4"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(9, 7, 9, 7),
            ToolTip = role is { ShowBadge: true } ? $"{columnName}: {role.Label} - {role.Description}" : columnName,
            Child = child
        };
        Grid.SetRow(border, 0);
        Grid.SetColumn(border, column);
        HtmlTableGrid.Children.Add(border);
    }

    private void AddHtmlDataCell(DataRow row, string columnName, bool writable, int gridRow, int gridColumn)
    {
        var value = row[columnName] == DBNull.Value ? "" : Convert.ToString(row[columnName]) ?? "";
        var hasLink = TryGetLinkedTarget(row, columnName, out var linkTarget);
        FrameworkElement child;
        if (writable && !columnName.Equals("Id", StringComparison.OrdinalIgnoreCase))
        {
            var box = new TextBox
            {
                Text = value,
                MinWidth = Math.Max(95, ColumnWidth(columnName) - 20),
                Margin = new Thickness(7, 8, 7, 8),
                Tag = (row, columnName),
                ToolTip = columnName
            };
            box.TextChanged += (_, _) =>
            {
                var (targetRow, targetColumn) = ((DataRow Row, string Column))box.Tag;
                object next = string.IsNullOrEmpty(box.Text) ? DBNull.Value : box.Text;
                if (!Equals(targetRow[targetColumn], next))
                {
                    targetRow[targetColumn] = next;
                }
            };
            if (hasLink)
            {
                child = LinkedTextBoxCell(box, linkTarget!);
            }
            else
            {
                child = box;
            }
        }
        else if (hasLink)
        {
            child = LinkedCellButton(value, linkTarget!);
        }
        else
        {
            child = new TextBlock
            {
                Text = value,
                Margin = new Thickness(9, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush(_darkMode ? "#d8eef3" : "#142025"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = value
            };
        }

        var border = new Border
        {
            MinHeight = 54,
            BorderBrush = Brush(_darkMode ? "#2d4047" : "#d6e0e4"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = gridRow % 2 == 0 ? Brush(_darkMode ? "#142226" : "#fbfcfd") : Brush(_darkMode ? "#121d20" : "#ffffff"),
            Child = child
        };
        Grid.SetRow(border, gridRow);
        Grid.SetColumn(border, gridColumn);
        HtmlTableGrid.Children.Add(border);
    }

    private Grid LinkedTextBoxCell(TextBox box, LinkTarget target)
    {
        box.Padding = new Thickness(54, box.Padding.Top, box.Padding.Right, box.Padding.Bottom);
        box.ToolTip = $"{box.ToolTip} | Double-click to open linked row";
        box.MouseDoubleClick += (_, _) => NavigateToLinkedTarget(target);

        var open = LinkedCellButton("Open", target);
        open.Width = 46;
        open.MinWidth = 46;
        open.Height = 24;
        open.MinHeight = 24;
        open.Padding = new Thickness(0);
        open.Margin = new Thickness(12, 0, 0, 0);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid
        {
            MinWidth = box.MinWidth,
            Children = { box, open }
        };
        return grid;
    }

    private Button LinkedCellButton(string textValue, LinkTarget target)
    {
        var button = new Button
        {
            Content = string.IsNullOrWhiteSpace(textValue) ? "Open" : textValue,
            MinHeight = 28,
            MinWidth = textValue.Equals("Open", StringComparison.OrdinalIgnoreCase) ? 54 : 70,
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(7, 8, 7, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brush(_darkMode ? "#0f2c34" : "#eefbff"),
            Foreground = Brush(_darkMode ? "#7ee7fb" : "#057c93"),
            BorderBrush = Brush(_darkMode ? "#1f6471" : "#8bd8e7"),
            ToolTip = $"Open {target.Table}.{target.Column} = {target.Value}",
            Cursor = Cursors.Hand,
            Tag = target
        };
        button.Click += (_, _) => NavigateToLinkedTarget(target);
        return button;
    }

    private bool TryGetLinkedTarget(DataRow row, string columnName, out LinkTarget? target)
    {
        target = null;
        if (_editor is null || _currentTableName is null || !row.Table.Columns.Contains(columnName))
        {
            return false;
        }

        var value = row[columnName];
        if (value is null || value == DBNull.Value || string.IsNullOrWhiteSpace(Convert.ToString(value)))
        {
            return false;
        }

        LinkTarget? candidate = columnName switch
        {
            "EngineID" when !_currentTableName.Equals("Data_Engine", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("Data_Engine", "EngineID", value),
            "MotorID" when !_currentTableName.Equals("Data_Motor", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("Data_Motor", "MotorID", value),
            "CarBodyID" or "CarBodyId" when !_currentTableName.Equals("Data_CarBody", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("Data_CarBody", "Id", value),
            "DrivetrainID" when !_currentTableName.Equals("Data_Drivetrain", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("Data_Drivetrain", "DrivetrainID", value),
            "TireCompoundID" or "TireCompoundId" when !_currentTableName.Equals("List_TireCompound", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_TireCompound", "TireCompoundID", value),
            "FrontSpringDamperPhysicsID" or "RearSpringDamperPhysicsID" when !_currentTableName.Equals("List_SpringDamperPhysics", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_SpringDamperPhysics", "SpringDamperPhysicsID", value),
            "AntiSwayPhysicsID" when !_currentTableName.Equals("List_AntiSwayPhysics", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_AntiSwayPhysics", "AntiSwayPhysicsID", value),
            "BrakesProfileID" or "BrakeProfile" when !_currentTableName.Equals("List_BrakeProfile", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_BrakeProfile", "BrakesProfileID", value),
            "AeroPhysicsID" when !_currentTableName.Equals("List_AeroPhysics", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_AeroPhysics", "AeroPhysicsID", value),
            "PreloadAndDroopDamperID" when !_currentTableName.Equals("List_PreloadAndDroopDamper", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_PreloadAndDroopDamper", "PreloadAndDroopDamperID", value),
            "FrontThirdSpringID" or "RearThirdSpringID" when !_currentTableName.Equals("List_ThirdSpringElement", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_ThirdSpringElement", "ThirdSpringID", value),
            "SteeringSettingsProfileID" when !_currentTableName.Equals("List_SteeringSettings", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_SteeringSettings", "SteeringSettingsID", value),
            "RearSteeringSettingsID" when !_currentTableName.Equals("List_RearSteeringSettings", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_RearSteeringSettings", "Id", value),
            "FrictionMultiCurveLateralID" or "FrictionMultiCurveLateralID_Offroad" or
            "FrictionMultiCurveLongitudinalAccelID" or "FrictionMultiCurveLongitudinalAccelID_Offroad" or
            "FrictionMultiCurveLongitudinalBrakeID" or "FrictionMultiCurveLongitudinalBrakeID_Offroad" or
            "FrictionMultiCurveLateralID_Snow" or "FrictionMultiCurveLongitudinalAccelID_Snow" or
            "FrictionMultiCurveLongitudinalBrakeID_Snow" when !_currentTableName.Equals("List_TireFrictionMultiCurve", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_TireFrictionMultiCurve", "FrictionMultiCurveID", value),
            "TireFrictionCurveID0" or "TireFrictionCurveID1" when !_currentTableName.Equals("List_TireFrictionCurve", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_TireFrictionCurve", "FrictionCurveID", value),
            _ when columnName.StartsWith("AffectCurve", StringComparison.OrdinalIgnoreCase) &&
                   columnName.EndsWith("ID", StringComparison.OrdinalIgnoreCase) &&
                   !_currentTableName.Equals("List_TireAffectCurve", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_TireAffectCurve", "AffectCurveID", value),
            "PartId" when _currentTableName.Equals("List_UpgradeTireCompoundFictionModOverride", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_UpgradeTireCompound", "Id", value),
            "UpgradeId" when _currentTableName.Equals("List_SpringDamperPhysics", StringComparison.OrdinalIgnoreCase) =>
                new LinkTarget("List_UpgradeSpringDamper", "Id", value),
            "UpgradeId" when _currentTableName.Equals("List_AntiSwayPhysics", StringComparison.OrdinalIgnoreCase) && row.Table.Columns.Contains("Axle") =>
                new LinkTarget(Convert.ToString(row["Axle"])?.Equals("Rear", StringComparison.OrdinalIgnoreCase) == true ? "List_UpgradeAntiSwayRear" : "List_UpgradeAntiSwayFront", "Id", value),
            "UpgradeId" when _currentTableName.Equals("List_AeroPhysics", StringComparison.OrdinalIgnoreCase) && row.Table.Columns.Contains("Part") =>
                new LinkTarget(Convert.ToString(row["Part"])?.Equals("FrontBumper", StringComparison.OrdinalIgnoreCase) == true ? "List_UpgradeCarBodyFrontBumper" : "List_UpgradeRearWing", "Id", value),
            _ => null
        };

        if (candidate is null || !_editor.TableExists(candidate.Table))
        {
            return false;
        }

        target = candidate;
        return true;
    }

    private void NavigateToLinkedTarget(LinkTarget target)
    {
        if (_editor is null)
        {
            return;
        }
        if (!TryApplyCurrentTableChanges(showSuccess: false))
        {
            return;
        }

        try
        {
            var tabIndex = TabIndexForTable(target.Table);
            if (EditorTabs.SelectedIndex != tabIndex)
            {
                EditorTabs.SelectedIndex = tabIndex;
            }
            else
            {
                PopulateTableChoices();
            }

            SelectTableChoice(target.Table, suppressSelectionChanged: true);
            _tableSearchText = "";
            if (!string.IsNullOrEmpty(TableSearchBox.Text))
            {
                TableSearchBox.Text = "";
            }

            _currentTableName = target.Table;
            _currentTable = _editor.LoadRowsWhere(target.Table, target.Column, target.Value);
            RenderCurrentTableView();
            UpdateTemplatePanel();
            UpdateTabToolPanels();
            UpdateTableStatus($"{target.Column} {target.Value}");
            AppendLog($"Opened linked row: {target.Table}.{target.Column} = {target.Value}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open linked row", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddHtmlActionCell(DataRow row, int gridRow, int gridColumn)
    {
        var save = HtmlActionButton("Save");
        save.Click += (_, _) => TryApplyCurrentTableChanges(showSuccess: true);

        var panel = new WrapPanel
        {
            Margin = new Thickness(7, 7, 7, 7)
        };
        panel.Children.Add(save);

        var clone = HtmlActionButton("Clone");
        clone.Click += (_, _) =>
        {
            if (_editor is null || _currentTableName is null)
            {
                return;
            }
            try
            {
                TryApplyCurrentTableChanges(showSuccess: false);
                _editor.CloneTableRow(_currentTableName, row);
                AppendLog($"{_currentTableName}: cloned row");
                ReloadCurrentTableAfterMutation();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Clone failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        var delete = HtmlActionButton("Delete");
        delete.Foreground = Brush(_darkMode ? "#ff6a61" : "#b63b32");
        delete.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(this, "Delete this row from the session DB?", "Delete row", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
            try
            {
                row.Delete();
                TryApplyCurrentTableChanges(showSuccess: false);
                if (_currentTableName is not null)
                {
                    ForceLiveReplace(_currentTableName);
                }
                ReloadCurrentTableAfterMutation();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        if (CanCloneDeleteCurrentRows())
        {
            panel.Children.Add(clone);
            panel.Children.Add(delete);
        }
        var border = new Border
        {
            BorderBrush = Brush(_darkMode ? "#2d4047" : "#d6e0e4"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = gridRow % 2 == 0 ? Brush(_darkMode ? "#142226" : "#fbfcfd") : Brush(_darkMode ? "#121d20" : "#ffffff"),
            Child = panel
        };
        Grid.SetRow(border, gridRow);
        Grid.SetColumn(border, gridColumn);
        HtmlTableGrid.Children.Add(border);
    }

    private bool CanCloneDeleteCurrentRows()
    {
        if (_currentTableName is null)
        {
            return false;
        }

        return _currentTableName.StartsWith("List_Upgrade", StringComparison.OrdinalIgnoreCase) ||
               EditorConstants.EnginePartTables.Contains(_currentTableName, StringComparer.OrdinalIgnoreCase);
    }

    private void ReloadCurrentTableAfterMutation()
    {
        if (SelectedTabIndex() == 2 &&
            PartEngineCombo.SelectedItem is EngineChoice &&
            PartTableCombo.SelectedItem is TableChoice table &&
            _currentTableName?.Equals(table.Name, StringComparison.OrdinalIgnoreCase) == true)
        {
            LoadSelectedPartRows();
            return;
        }

        LoadSelectedTable();
    }

    private Button HtmlActionButton(string textValue)
    {
        return new Button
        {
            Content = textValue,
            MinHeight = 30,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 7, 7),
            Background = Brush(_darkMode ? "#101719" : "#ffffff"),
            Foreground = Brush(_darkMode ? "#e5eff2" : "#142025"),
            BorderBrush = Brush(_darkMode ? "#2d4047" : "#d6e0e4"),
            Cursor = Cursors.Hand
        };
    }

    private IReadOnlyList<string> VisibleColumnsForTable(string table, DataTable data)
    {
        string[] preferred = table switch
        {
            "List_UpgradeEngine" =>
            [
                "Id", "EngineMediaName", "EngineID", "Level", "IsStock", "Price", "MassDiff", "EffectiveMenuMassDiffKg",
                "EngineMassDeltaKg", "EngineMassKg", "StockEngineMassKg", "WeightDistDiff", "EngineGraphingMaxPower", "EngineGraphingMaxTorque"
            ],
            "Data_Car" =>
            [
                "Id", "Year", "MakeID", "MediaName", "CurbWeight", "CurbWeightKg", "WeightDistribution",
                "FrontTireWidthMM", "RearTireWidthMM", "FrontStockRideHeight", "RearStockRideHeight",
                "PerformanceIndex", "PI", "ClassID"
            ],
            "List_UpgradeTireCompound" =>
            [
                "Id", "TireModelName", "WetTireModelName", "TireCompoundID", "TireCompoundName", "Ordinal",
                "Level", "IsStock", "Price", "MassDiff", "FrontTirePressure", "RearTirePressure", "BaseDefaultPressure"
            ],
            "List_TireCompound" =>
            [
                "TireCompoundID", "DisplayName", "KnownUpgradeLabels", "DefaultPressure", "TorqueFreeLatFrictionScale",
                "TorqueFreeLongFrictionScaleBrake", "TorqueFreeLongFrictionScaleAccel0", "WetFrictionModFrictionScale",
                "WetFrictionModSlipScale", "TireRollResistance", "IsOffroad", "FrictionMultiCurveLateralID", "FrictionMultiCurveLongitudinalAccelID"
            ],
            "List_TyreCurveDB" =>
            [
                "TireCompoundID", "DisplayName", "KnownUpgradeLabels", "Asph_LatSlipPeak0", "Asph_LatSlipPeak1",
                "Asph_LongSlipPeak0", "Asph_LongSlipPeak1", "Asph_BrkSlipPeak0", "Asph_BrkSlipPeak1",
                "Off_LatSlipPeak0", "Off_LatSlipPeak1", "Snow_LatSlipPeak0", "Snow_LatSlipPeak1"
            ],
            "Combo_TireBrandCompound" =>
            [
                "Id", "TireBrandId", "TireCompoundId", "TireCompoundName", "KnownUpgradeLabels", "PriceScale",
                "WearScale", "FrictionScale", "PeakSASRScale", "PriceDisplay", "WearDisplay", "FrictionDisplay", "PeakSASRDisplay"
            ],
            "List_UpgradeTireCompoundFictionModOverride" =>
            [
                "PartId", "TireModelName", "TireCompoundID", "TireCompoundName", "FrontLatFrictionMult",
                "FrontLongFrictionMult", "RearLatFrictionMult", "RearLongFrictionMult"
            ],
            "List_UpgradeCarBodyWeight" =>
            [
                "Id", "CarBodyId", "Level", "IsStock", "Mass", "InitialMass", "EffectiveMassDiffKg",
                "CMHeight", "CMBackFront", "CMLeftRight", "BlockDimX", "BlockDimY", "BlockDimZ", "Price"
            ],
            "List_UpgradeSpringDamper" =>
            [
                "Id", "Ordinal", "Level", "IsStock", "FrontSpringDamperPhysicsID", "RearSpringDamperPhysicsID",
                "FrontMinRideHeight", "FrontDefRideHeight", "FrontMaxRideHeight",
                "RearMinRideHeight", "RearDefRideHeight", "RearMaxRideHeight",
                "FrontMinSpringRate", "FrontMaxSpringRate", "RearMinSpringRate", "RearMaxSpringRate"
            ],
            "List_SpringDamperPhysics" =>
            [
                "SpringDamperPhysicsID", "Axle", "UpgradeId", "UpgradeLevel", "UpgradeIsStock",
                "DefRideHeight", "MinRideHeight", "MaxRideHeight", "DefSpringRate", "MinSpringRate", "MaxSpringRate",
                "DefDampenBumpRate", "MinDampenBumpRate", "MaxDampenBumpRate",
                "DefDampenReboundRate", "MinDampenReboundRate", "MaxDampenReboundRate"
            ],
            "List_UpgradeAntiSwayFront" =>
            [
                "Id", "Ordinal", "Level", "IsStock", "AntiSwayPhysicsID", "FrontDefSwaybar",
                "FrontMinSwaybar", "FrontMaxSwaybar", "FrontSwaybarDamping", "MassDiff", "Price"
            ],
            "List_UpgradeAntiSwayRear" =>
            [
                "Id", "Ordinal", "Level", "IsStock", "AntiSwayPhysicsID", "RearDefSwaybar",
                "RearMinSwaybar", "RearMaxSwaybar", "RearSwaybarDamping", "MassDiff", "Price"
            ],
            "List_AntiSwayPhysics" =>
            [
                "AntiSwayPhysicsID", "Axle", "UpgradeId", "UpgradeLevel", "UpgradeIsStock",
                "DefSwaybarStiffness", "MinSwaybarStiffness", "MaxSwaybarStiffness", "SwaybarDamping", "BeamStiffness"
            ],
            "List_UpgradeBrakes" =>
            [
                "Id", "Ordinal", "Level", "IsStock", "BrakeProfileName", "BrakesProfileID", "GameFrictionScaleBraking",
                "BrakeTorqueSlider", "BrakeBiasSlider", "FrontBrakeTorqueClamp", "RearBrakeTorqueClamp", "MassDiff", "Price"
            ],
            "List_UpgradeDrivetrainTransmission" =>
            [
                "Id", "DrivetrainID", "Level", "IsStock", "FinalDriveRatio", "NumGears", "GearRatio0", "GearRatio1",
                "GearRatio2", "GearRatio3", "GearRatio4", "GearRatio5", "GearRatio6", "GearShiftTime", "MassDiff", "Price"
            ],
            "List_UpgradeDrivetrainDifferential" =>
            [
                "Id", "DrivetrainID", "Level", "IsStock", "FrontLimitedSlipTorqueAccel", "FrontLimitedSlipTorqueDecel",
                "RearLimitedSlipTorqueAccel", "RearLimitedSlipTorqueDecel", "CenterLimitedSlipTorqueAccel",
                "CenterLimitedSlipTorqueDecel", "RearToqueSplit", "TCProfileID", "DifferentialProfileID", "Price"
            ],
            "List_AeroPhysics" =>
            [
                "AeroPhysicsID", "Part", "UpgradeId", "UpgradeLevel", "UpgradeIsStock", "DefaultTuneSlider",
                "Drag0", "Downforce0", "Drag1", "Downforce1", "AngleZeroDownforce", "LateralDrag"
            ],
            "List_UpgradeRearWing" =>
            [
                "Id", "Ordinal", "Level", "IsStock", "Sequence", "AeroPhysicsID", "DefaultTuneSlider",
                "Drag0", "Downforce0", "Drag1", "Downforce1", "MassDiff", "DragScale", "WindInstabilityScale", "Price"
            ],
            "List_UpgradeCarBodyFrontBumper" =>
            [
                "Id", "CarBodyID", "Level", "IsStock", "Sequence", "AeroPhysicsID", "DefaultTuneSlider",
                "Drag0", "Downforce0", "Drag1", "Downforce1", "MassDiff", "DragScale", "WindInstabilityScale", "Price"
            ],
            _ when EditorConstants.EnginePartTables.Contains(table, StringComparer.OrdinalIgnoreCase) =>
            [
                "Id", "EngineID", "Level", "IsStock", "ManufacturerID", "PartsStringID", "PartsStringId", "Price",
                "MassDiff", "DragScale", "WindInstabilityScale", "MaxScale", "PowerMaxScale", "MinScale", "PowerMinScale", "RobScale", "TorqueScale"
            ],
            _ when EditorConstants.AeroTables.Contains(table, StringComparer.OrdinalIgnoreCase) =>
            [
                "Id", "Ordinal", "CarBodyID", "CarBodyId", "Sequence", "IsStock", "AeroPhysicsID", "Price",
                "Drag0", "Downforce0", "Drag1", "Downforce1"
            ],
            "NewProfile_Career_Garage" =>
            [
                "Id", "CarId", "PerformanceIndex", "ClassID", "SpeedRating", "HandlingRating", "AccelerationRating",
                "LaunchRating", "BrakingRating", "CurbWeight", "Tuning_frontTirePressure", "Tuning_rearTirePressure"
            ],
            "Data_Engine" =>
            [
                "EngineID", "MediaName", "EngineMass-kg", "ConfigID", "CylinderID", "Compression",
                "AspirationIDStock", "StockBoost-bar", "EngineGraphingMaxPower", "EngineGraphingMaxTorque"
            ],
            "Data_Motor" =>
            [
                "MotorID", "MediaName", "MotorName", "PowerMax", "TorqueMax", "MotorMass", "TorqueScale"
            ],
            _ =>
            [
                "Id", "Ordinal", "CarId", "CarID", "CarBodyID", "EngineID", "Level", "IsStock", "Price",
                "MediaName", "DisplayName", "Name", "Value"
            ]
        };

        var existing = data.Columns.Cast<DataColumn>()
            .Select(c => c.ColumnName)
            .Where(c => c != "__fh6_rowid")
            .ToList();
        var chosen = new List<string>();
        foreach (var column in preferred)
        {
            var actual = existing.FirstOrDefault(c => c.Equals(column, StringComparison.OrdinalIgnoreCase));
            if (actual is not null && !chosen.Contains(actual, StringComparer.OrdinalIgnoreCase))
            {
                chosen.Add(actual);
            }
        }
        foreach (var column in existing)
        {
            if (chosen.Count >= 13)
            {
                break;
            }
            if (!chosen.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                chosen.Add(column);
            }
        }
        return chosen;
    }

    private static double ColumnWidth(string columnName)
    {
        if (columnName.Contains("MediaName", StringComparison.OrdinalIgnoreCase) ||
            columnName.Contains("Name", StringComparison.OrdinalIgnoreCase) ||
            columnName.Contains("String", StringComparison.OrdinalIgnoreCase) ||
            columnName.Contains("Label", StringComparison.OrdinalIgnoreCase) ||
            columnName.Contains("Compound", StringComparison.OrdinalIgnoreCase))
        {
            return 220;
        }
        if (columnName.Contains("Graphing", StringComparison.OrdinalIgnoreCase) ||
            columnName.Contains("Instability", StringComparison.OrdinalIgnoreCase))
        {
            return 170;
        }
        return 135;
    }

    private void RenderFieldForm(DataRow row)
    {
        FieldFormPanel.Children.Clear();
        AddFieldFormActions(row);
        var writable = _editor is not null && _currentTableName is not null
            ? _editor.WritableColumnNames(_currentTableName)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in FieldFormColumns(row))
        {
            if (column.ColumnName == "__fh6_rowid")
            {
                continue;
            }

            var isCurbWeightKg = IsCurbWeightKgColumn(column.ColumnName, row);
            var canEdit = (writable.Contains(column.ColumnName) || isCurbWeightKg) &&
                          !column.ColumnName.Equals("Id", StringComparison.OrdinalIgnoreCase);

            var label = new TextBlock
            {
                Text = FieldLabelText(column.ColumnName, isCurbWeightKg),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = FieldToolTip(column.ColumnName, isCurbWeightKg),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var valueBox = new TextBox
            {
                Text = row[column] == DBNull.Value ? "" : Convert.ToString(row[column]) ?? "",
                MinWidth = 190,
                MaxWidth = 260,
                IsReadOnly = !canEdit,
                ToolTip = FieldToolTip(column.ColumnName, isCurbWeightKg),
                Tag = column.ColumnName
            };
            if (canEdit)
            {
                valueBox.TextChanged += (_, _) =>
                {
                    var columnName = (string)valueBox.Tag;
                    if (IsCurbWeightKgColumn(columnName, row))
                    {
                        if (TryParseUiDouble(valueBox.Text, out var kg))
                        {
                            row["CurbWeightKg"] = kg;
                            row["CurbWeight"] = kg / 100.0;
                        }
                        return;
                    }

                    object next = string.IsNullOrEmpty(valueBox.Text) ? DBNull.Value : valueBox.Text;
                    if (!Equals(row[columnName], next))
                    {
                        row[columnName] = next;
                    }
                };
            }

            var card = new Border
            {
                Width = 245,
                MinHeight = 72,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(8),
                Child = new StackPanel
                {
                    Children =
                    {
                        label,
                        valueBox
                    }
                }
            };
            ApplyFieldCardTheme(card, label, valueBox);
            FieldFormPanel.Children.Add(card);
        }
    }

    private void AddFieldFormActions(DataRow row)
    {
        if (!CanCloneDeleteCurrentRows())
        {
            return;
        }

        var panel = new WrapPanel();
        var save = HtmlActionButton("Save");
        save.Click += (_, _) => TryApplyCurrentTableChanges(showSuccess: true);
        panel.Children.Add(save);

        var clone = HtmlActionButton("Clone");
        clone.Click += (_, _) =>
        {
            if (_editor is null || _currentTableName is null)
            {
                return;
            }
            try
            {
                TryApplyCurrentTableChanges(showSuccess: false);
                _editor.CloneTableRow(_currentTableName, row);
                AppendLog($"{_currentTableName}: cloned row");
                ReloadCurrentTableAfterMutation();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Clone failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        panel.Children.Add(clone);

        var delete = HtmlActionButton("Delete");
        delete.Foreground = Brush(_darkMode ? "#ff6a61" : "#b63b32");
        delete.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(this, "Delete this row from the session DB?", "Delete row", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
            try
            {
                row.Delete();
                TryApplyCurrentTableChanges(showSuccess: false);
                if (_currentTableName is not null)
                {
                    ForceLiveReplace(_currentTableName);
                }
                ReloadCurrentTableAfterMutation();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        panel.Children.Add(delete);

        var card = new Border
        {
            Width = 520,
            MinHeight = 46,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Child = panel
        };
        ApplyFieldCardTheme(card, null, null);
        FieldFormPanel.Children.Add(card);
    }

    private IReadOnlyList<DataColumn> FieldFormColumns(DataRow row)
    {
        var columns = row.Table.Columns.Cast<DataColumn>()
            .Where(c => c.ColumnName != "__fh6_rowid")
            .ToList();
        if (_currentTableName?.Equals("Data_Car", StringComparison.OrdinalIgnoreCase) != true)
        {
            return columns;
        }

        string[] preferred =
        [
            "Id", "Year", "MakeID", "MediaName", "ClassID", "PI", "PerformanceIndex",
            "CurbWeightKg", "CurbWeight", "WeightDistribution",
            "FrontTireWidthMM", "RearTireWidthMM", "FrontStockRideHeight", "RearStockRideHeight"
        ];

        var ordered = new List<DataColumn>();
        foreach (var name in preferred)
        {
            var column = columns.FirstOrDefault(c => c.ColumnName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (column is not null && !ordered.Contains(column))
            {
                ordered.Add(column);
            }
        }
        ordered.AddRange(columns.Where(c => !ordered.Contains(c)));
        return ordered;
    }

    private bool IsCurbWeightKgColumn(string columnName, DataRow row)
    {
        return _currentTableName?.Equals("Data_Car", StringComparison.OrdinalIgnoreCase) == true &&
               columnName.Equals("CurbWeightKg", StringComparison.OrdinalIgnoreCase) &&
               row.Table.Columns.Contains("CurbWeight");
    }

    private static bool TryParseUiDouble(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
               double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
               double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private bool TryApplyCurrentTableChanges(bool showSuccess)
    {
        if (_editor is null || _currentTable is null || _currentTableName is null)
        {
            return true;
        }

        try
        {
            TableGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            TableGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var count = _editor.ApplyTableChanges(_currentTableName, _currentTable);
            if (count > 0 || showSuccess)
            {
                AppendLog($"{_currentTableName}: applied {count} edit(s) to session DB");
                TableStatusText.Text = $"{_currentTableName}: applied {count} edit(s)";
            }
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Apply edits failed: {ex.Message}");
            if (showSuccess)
            {
                MessageBox.Show(this, ex.Message, "Apply Edits failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }
    }

    private void UpdateTemplatePanel()
    {
        if (_editor is null || _currentTableName is null || SelectedTabIndex() == 2 || !EditorConstants.EnginePartTables.Contains(_currentTableName))
        {
            TemplatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        TemplatePanel.Visibility = Visibility.Visible;
        if (_selectedCar is not null && string.IsNullOrWhiteSpace(TemplateEngineIdBox.Text))
        {
            var engineId = _editor.EngineIdsForCar(_selectedCar.Id).FirstOrDefault();
            if (engineId != 0)
            {
                TemplateEngineIdBox.Text = engineId.ToString();
            }
        }

        long? targetEngineId = long.TryParse(TemplateEngineIdBox.Text, out var parsed) ? parsed : null;
        var templates = _editor.EnginePartTemplates(_currentTableName, targetEngineId)
            .Select(t => (t.SourceTable, t.SourceId, t.Label))
            .ToList();
        TemplateCombo.ItemsSource = templates;
        TemplateCombo.DisplayMemberPath = "Item3";
        TemplateCombo.SelectedIndex = templates.Count > 0 ? 0 : -1;
    }

    private void UpdateTabToolPanels()
    {
        var isEnginesTab = SelectedTabIndex() == 1;
        var isPartsTab = SelectedTabIndex() == 2;
        var isValidateTab = IsValidateTab();
        EngineToolsPanel.Visibility = isEnginesTab ? Visibility.Visible : Visibility.Collapsed;
        PartToolsPanel.Visibility = isPartsTab ? Visibility.Visible : Visibility.Collapsed;
        TableToolbar.Visibility = isValidateTab ? Visibility.Collapsed : Visibility.Visible;
        UpdateAddLinkedOptionButton();
        UpdateWireMenuButton();
        if (isEnginesTab)
        {
            RefreshEngineTools();
        }
        if (isPartsTab)
        {
            RefreshPartTools();
        }
    }

    private void RefreshEngineTools()
    {
        if (_editor is null || SelectedTabIndex() != 1)
        {
            return;
        }

        _loadingEngineTools = true;
        try
        {
            var selectedEngineId = (CarEngineCombo.SelectedItem as EngineChoice)?.EngineId;
            var carEngines = _selectedCar is null ? [] : _editor.EngineSwapsForCar(_selectedCar.Id).ToList();
            CarEngineCombo.ItemsSource = carEngines;
            CarEngineCombo.DisplayMemberPath = nameof(EngineChoice.Label);
            if (selectedEngineId.HasValue)
            {
                CarEngineCombo.SelectedItem = carEngines.FirstOrDefault(e => e.EngineId == selectedEngineId.Value);
            }
            if (CarEngineCombo.SelectedIndex < 0 && carEngines.Count > 0)
            {
                CarEngineCombo.SelectedIndex = 0;
            }

            RefreshEngineCatalog();
            EnginePanelNote.Text = _selectedCar is null
                ? "Select a car to add engine swaps."
                : "Adds a List_UpgradeEngine row for the selected car using the stock row as a template.";
        }
        finally
        {
            _loadingEngineTools = false;
        }
    }

    private void RefreshEngineCatalog()
    {
        if (_editor is null)
        {
            return;
        }

        var selected = (EngineCatalogCombo.SelectedItem as EngineChoice)?.EngineId;
        var engines = _editor.SearchEngineCatalog(EngineSearchBox.Text).ToList();
        EngineCatalogCombo.ItemsSource = engines;
        EngineCatalogCombo.DisplayMemberPath = nameof(EngineChoice.Label);
        if (selected.HasValue)
        {
            EngineCatalogCombo.SelectedItem = engines.FirstOrDefault(e => e.EngineId == selected.Value);
        }
        if (EngineCatalogCombo.SelectedIndex < 0 && engines.Count > 0)
        {
            EngineCatalogCombo.SelectedIndex = 0;
        }
    }

    private bool SelectTableChoice(string tableName, bool suppressSelectionChanged = false)
    {
        foreach (var item in TableCombo.Items)
        {
            if (item is TableChoice choice && choice.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            {
                if (suppressSelectionChanged)
                {
                    _suppressTableSelectionChanged = true;
                    try
                    {
                        TableCombo.SelectedItem = item;
                    }
                    finally
                    {
                        _suppressTableSelectionChanged = false;
                    }
                }
                else
                {
                    TableCombo.SelectedItem = item;
                }
                return true;
            }
        }
        return false;
    }

    private bool TryGetCurrentEngineId(out long engineId)
    {
        if (CarEngineCombo.SelectedItem is EngineChoice selected)
        {
            engineId = selected.EngineId;
            return true;
        }

        if (_editor is not null && _selectedCar is not null)
        {
            var first = _editor.EngineSwapsForCar(_selectedCar.Id).FirstOrDefault();
            if (first is not null)
            {
                engineId = first.EngineId;
                return true;
            }
        }

        engineId = 0;
        return false;
    }

    private bool TryGetSelectedPartEngineId(out long engineId)
    {
        if (PartEngineCombo.SelectedItem is EngineChoice selected)
        {
            engineId = selected.EngineId;
            return true;
        }

        return TryGetCurrentEngineId(out engineId);
    }

    private void SyncPartTableToolChoice(string tableName)
    {
        if (PartTableCombo.SelectedItem is TableChoice selected &&
            selected.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var item in PartTableCombo.Items)
        {
            if (item is TableChoice choice && choice.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            {
                _loadingPartTools = true;
                try
                {
                    PartTableCombo.SelectedItem = item;
                }
                finally
                {
                    _loadingPartTools = false;
                }
                RefreshPartTemplates();
                return;
            }
        }
    }

    private void RefreshPartTools()
    {
        if (_editor is null || SelectedTabIndex() != 2)
        {
            return;
        }

        _loadingPartTools = true;
        try
        {
            var selectedEngineId = (PartEngineCombo.SelectedItem as EngineChoice)?.EngineId;
            var engines = _selectedCar is null ? [] : _editor.EngineSwapsForCar(_selectedCar.Id).ToList();
            PartEngineCombo.ItemsSource = engines;
            PartEngineCombo.DisplayMemberPath = nameof(EngineChoice.Label);
            if (selectedEngineId.HasValue)
            {
                PartEngineCombo.SelectedItem = engines.FirstOrDefault(e => e.EngineId == selectedEngineId.Value);
            }
            if (PartEngineCombo.SelectedIndex < 0 && engines.Count > 0)
            {
                PartEngineCombo.SelectedIndex = 0;
            }

            var selectedTable = (PartTableCombo.SelectedItem as TableChoice)?.Name;
            var tables = _editor.ExistingTables(EditorConstants.EnginePartTables).Select(t => new TableChoice(t)).ToList();
            PartTableCombo.ItemsSource = tables;
            if (!string.IsNullOrWhiteSpace(selectedTable))
            {
                PartTableCombo.SelectedItem = tables.FirstOrDefault(t => t.Name == selectedTable);
            }
            if (PartTableCombo.SelectedIndex < 0 && tables.Count > 0)
            {
                PartTableCombo.SelectedIndex = 0;
            }

            PartPanelNote.Text = _selectedCar is null
                ? "Select a car to choose one of its engines."
                : "Pick an engine and part table, then copy a compatible template row into that engine.";
            UpdateAspirationTypeButton();
            UpdateProbeMenuButton();
        }
        finally
        {
            _loadingPartTools = false;
        }

        RefreshPartTemplates();
        RefreshAspirationConversions();
    }

    private TemplateChoice? SelectedPartTemplate()
    {
        if (PartTemplateCombo.SelectedItem is TemplateChoice selected &&
            !string.IsNullOrWhiteSpace(selected.SourceTable))
        {
            return selected;
        }

        return PartTemplateCombo.Items
            .OfType<TemplateChoice>()
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.SourceTable));
    }

    private void RefreshPartTemplates()
    {
        if (_editor is null ||
            PartEngineCombo.SelectedItem is not EngineChoice engine ||
            PartTableCombo.SelectedItem is not TableChoice table)
        {
            PartTemplateCombo.ItemsSource = null;
            UpdateAspirationTypeButton();
            UpdateProbeMenuButton();
            RefreshAspirationConversions();
            return;
        }

        var selected = PartTemplateCombo.SelectedItem as TemplateChoice;
        var templates = _editor.EnginePartTemplates(table.Name, engine.EngineId)
            .Select(t => new TemplateChoice(t.SourceTable, t.SourceId, t.Label))
            .ToList();
        var choices = new List<TemplateChoice>();
        if (templates.Count > 0)
        {
            choices.Add(new TemplateChoice("", 0, "Auto template for this engine/table"));
            choices.AddRange(templates);
        }
        PartTemplateCombo.ItemsSource = choices;
        PartTemplateCombo.DisplayMemberPath = nameof(TemplateChoice.Label);
        if (selected is not null)
        {
            PartTemplateCombo.SelectedItem = choices.FirstOrDefault(t => t.SourceTable == selected.SourceTable && t.SourceId == selected.SourceId);
        }
        if (PartTemplateCombo.SelectedIndex < 0 && choices.Count > 0)
        {
            PartTemplateCombo.SelectedIndex = 0;
        }

        if (_selectedCar is not null)
        {
            var hasCrossTable = templates.Any(t => !t.SourceTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase));
            var observedMax = EditorConstants.ObservedEnginePartMenuMaxLevel(table.Name);
            PartPanelNote.Text = templates.Count == 0
                ? "No template rows exist for this part type yet."
                : hasCrossTable
                    ? "Templates include compatible rows from other boost/supercharger tables, useful for empty part types like Quad Turbo."
                    : "Templates are existing rows from this part table. Auto picks the closest one.";
            if (table.Name.Equals("List_UpgradeEngineTurboQuad", StringComparison.OrdinalIgnoreCase))
            {
                PartPanelNote.Text += " Quad Turbo needs both these part rows and generated QuadTurbo menu metadata to appear in the Engine menu.";
            }
            if (observedMax.HasValue)
            {
                PartPanelNote.Text += $" Base data has no visible {table.Name.Replace("List_UpgradeEngine", "", StringComparison.OrdinalIgnoreCase)} menu levels above {observedMax.Value}; higher added levels are experimental and may not show in-game.";
            }
        }
        UpdateAspirationTypeButton();
        UpdateProbeMenuButton();
        RefreshAspirationConversions();
    }

    private void RefreshAspirationConversions()
    {
        if (_editor is null || SelectedTabIndex() != 2 || PartEngineCombo.SelectedItem is not EngineChoice engine)
        {
            AspirationConversionCombo.ItemsSource = null;
            AddAspirationConversionButton.IsEnabled = false;
            RemoveAspirationConversionButton.IsEnabled = false;
            return;
        }

        var selected = AspirationConversionCombo.SelectedItem as AspirationConversionChoice;
        var choices = _editor.AspirationConversionChoices(engine.EngineId);
        AspirationConversionCombo.ItemsSource = choices;
        AspirationConversionCombo.DisplayMemberPath = nameof(AspirationConversionChoice.Label);
        if (selected is not null)
        {
            AspirationConversionCombo.SelectedItem = choices.FirstOrDefault(c => c.AspirationId == selected.AspirationId);
        }
        if (AspirationConversionCombo.SelectedIndex < 0)
        {
            var currentTable = (PartTableCombo.SelectedItem as TableChoice)?.Name;
            AspirationConversionCombo.SelectedItem = choices.FirstOrDefault(c => currentTable is not null && c.TableName.Equals(currentTable, StringComparison.OrdinalIgnoreCase))
                                                     ?? choices.FirstOrDefault(c => !c.IsStock)
                                                     ?? choices.FirstOrDefault();
        }

        AddAspirationConversionButton.IsEnabled = choices.Count > 0;
        AddAspirationConversionButton.ToolTip = choices.Count > 0
            ? "Adds the Body Kits/Conversions aspiration availability row and matching engine part levels for the selected engine."
            : null;
        RemoveAspirationConversionButton.IsEnabled = choices.Count > 0;
        RemoveAspirationConversionButton.ToolTip = choices.Count > 0
            ? "Deletes this engine's rows from the mapped aspiration part table and forces that table to refresh in the live game on next import."
            : null;
    }

    private void ForceLiveReplace(params string[] tables)
    {
        foreach (var table in tables)
        {
            if (!string.IsNullOrWhiteSpace(table))
            {
                _forceReplaceOnNextImport.Add(table);
            }
        }
    }

    private void UpdateAspirationTypeButton()
    {
        var table = (PartTableCombo.SelectedItem as TableChoice)?.Name;
        var visible = SelectedTabIndex() == 2 &&
                      table is not null &&
                      _editor?.CanWireMenuMetadata(table) == true;
        EnableAspirationTypeButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        EnableAspirationTypeButton.ToolTip = visible
            ? "Creates missing global menu metadata for this part table, including extra Level rows and dormant types such as Quad Turbo."
            : null;
    }

    private void UpdateProbeMenuButton()
    {
        var visible = SelectedTabIndex() == 2 &&
                      PartEngineCombo.SelectedItem is EngineChoice &&
                      PartTableCombo.SelectedItem is TableChoice;
        ProbeMenuButton.IsEnabled = visible;
        ProbeMenuButton.ToolTip = visible
            ? "Read-only diagnostic for this engine/table: part levels, UpgradeTypes, Upgrades metadata, aspiration mapping, duplicates, and likely menu cap issues."
            : null;
        ProbeAllMenusButton.IsEnabled = SelectedTabIndex() == 2 && PartEngineCombo.SelectedItem is EngineChoice;
        ProbeAllMenusButton.ToolTip = ProbeAllMenusButton.IsEnabled
            ? "Read-only summary of every engine part table for this engine. Use it to compare inferred base-data max levels before proving a real in-game cap."
            : null;
    }

    private void LoadSelectedPartRows()
    {
        if (_editor is null ||
            PartEngineCombo.SelectedItem is not EngineChoice engine ||
            PartTableCombo.SelectedItem is not TableChoice table)
        {
            return;
        }

        TryApplyCurrentTableChanges(showSuccess: false);
        SelectTableChoice(table.Name);
        _currentTableName = table.Name;
        _currentTable = _editor.LoadEngineParts(table.Name, engine.EngineId);
        RenderCurrentTableView();
        UpdateTableStatus($"EngineID {engine.EngineId}");
        UpdateAddLinkedOptionButton();
        UpdateWireMenuButton();
    }

    private void UpdateAddLinkedOptionButton()
    {
        var canAdd = _selectedCar is not null &&
                     _currentTableName is not null &&
                     !IsValidateTab() &&
                     (EditorConstants.UpgradeTableLinks.ContainsKey(_currentTableName) ||
                      EditorConstants.AeroOptionTables.Contains(_currentTableName, StringComparer.OrdinalIgnoreCase));
        AddLinkedOptionButton.Visibility = canAdd ? Visibility.Visible : Visibility.Collapsed;
        AddLinkedOptionButton.ToolTip = canAdd
            ? "Add another option for this selected car/body/drivetrain/motor target using an existing row as a template."
            : null;
    }

    private void UpdateWireMenuButton()
    {
        var canWire = _editor is not null &&
                      _currentTableName is not null &&
                      !IsValidateTab() &&
                      _editor.CanWireMenuMetadata(_currentTableName);
        WireMenuButton.Visibility = canWire ? Visibility.Visible : Visibility.Collapsed;
        WireMenuButton.ToolTip = canWire
            ? "Create missing global menu metadata for this table's current levels. Use this after manual row edits or old added rows."
            : null;
    }

    private async Task<bool> RunToolAsync(string title, Func<CancellationToken, Task> action)
    {
        _taskCts = new CancellationTokenSource();
        SetBusy(true);
        AppendLog(title);
        try
        {
            await action(_taskCts.Token);
            if (!_isClosing)
            {
                TableStatusText.Text = "Ready";
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                AppendLog("Cancelled.");
                TableStatusText.Text = "Cancelled.";
            }
            return false;
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                AppendLog("Failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
                TableStatusText.Text = "Failed.";
            }
            return false;
        }
        finally
        {
            if (!_isClosing)
            {
                SetBusy(false);
            }
            _taskCts.Dispose();
            _taskCts = null;
        }
    }

    private bool EnsureIdle()
    {
        if (_taskCts is null)
        {
            return true;
        }

        MessageBox.Show(this, "A dump/import task is already running.", "FH6 SQLite Editor", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void SetBusy(bool busy)
    {
        OpenDbButton.IsEnabled = !busy;
        DumpButton.IsEnabled = !busy;
        SaveAsButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        ResetGameButton.IsEnabled = !busy;
        ValidateButton.IsEnabled = !busy;
        ApplyChangesButton.IsEnabled = !busy;
        CancelTaskButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ConnectionStateText.Text = busy ? "Working" : "Local";
    }

    private int SelectedTabIndex() => EditorTabs.SelectedIndex < 0 ? 0 : EditorTabs.SelectedIndex;

    private bool IsValidateTab() => SelectedTabIndex() == 8;

    private bool IsCarScopedTab()
    {
        return SelectedTabIndex() is 0 or 1 or 2 or 3 or 4 or 5 or 7;
    }

    private static int TabIndexForTable(string tableName)
    {
        if (EditorConstants.CoreTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (EditorConstants.EngineTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (EditorConstants.EnginePartTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
        {
            return 2;
        }
        if (EditorConstants.TuningLimitTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
        {
            return 7;
        }
        if (EditorConstants.UpgradeTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
        {
            return 4;
        }
        if (EditorConstants.AeroTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
        {
            return 5;
        }
        if (EditorConstants.LookupTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
        {
            return 6;
        }
        if (EditorConstants.DefaultTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
        {
            return 3;
        }
        return 9;
    }

    private void RenderValidateView()
    {
        _currentTable = null;
        _currentTableName = null;
        TableGrid.ItemsSource = null;
        TableGrid.Visibility = Visibility.Collapsed;
        FieldFormBorder.Visibility = Visibility.Collapsed;
        HtmlTableBorder.Visibility = Visibility.Collapsed;
        MenuProbePanel.Visibility = Visibility.Collapsed;
        ValidatePanel.Visibility = Visibility.Visible;
        ValidateResultBox.Text = "Run the integrity check to validate the current session database.";
        TableStatusText.Text = "Validation ready.";
    }

    private void RenderMenuProbeView(string report)
    {
        TableGrid.ItemsSource = null;
        TableGrid.Visibility = Visibility.Collapsed;
        FieldFormBorder.Visibility = Visibility.Collapsed;
        HtmlTableBorder.Visibility = Visibility.Collapsed;
        ValidatePanel.Visibility = Visibility.Collapsed;
        HtmlTableGrid.Children.Clear();
        FieldFormPanel.Children.Clear();
        MenuProbePanel.Visibility = Visibility.Visible;
        MenuProbeResultBox.Text = report;
        MenuProbeResultBox.ScrollToHome();
        TableStatusText.Text = "Menu probe ready.";
    }

    private void RunValidation(bool showDialog)
    {
        if (_editor is null)
        {
            MessageBox.Show(this, "Open a database first.", "FH6 SQLite Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryApplyCurrentTableChanges(showSuccess: false))
        {
            return;
        }

        var result = _editor.Validate();
        var message = "SQLite integrity_check:" + Environment.NewLine + result;
        ValidateResultBox.Text = message;
        AppendLog("Integrity check: " + result.Replace(Environment.NewLine, " | "));
        TableStatusText.Text = result == "ok" ? "Validation passed." : "Validation returned warnings.";
        if (showDialog)
        {
            MessageBox.Show(this, result, "SQLite Integrity Check", MessageBoxButton.OK, result == "ok" ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(message));
            return;
        }

        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void ApplyTheme()
    {
        var bg = Brush(_darkMode ? "#101719" : "#eef2f4");
        var surface = Brush(_darkMode ? "#172124" : "#ffffff");
        var chrome = Brush(_darkMode ? "#141d20" : "#fbfcfd");
        var soft = Brush(_darkMode ? "#1d2a2f" : "#f6f8f9");
        var line = Brush(_darkMode ? "#2d4047" : "#d6e0e4");
        var text = Brush(_darkMode ? "#e5eff2" : "#142025");
        var muted = Brush(_darkMode ? "#98a9ae" : "#64747b");
        var accent = Brush(_darkMode ? "#22a5ba" : "#086f83");
        var selection = Brush(_darkMode ? "#264852" : "#c8eaf0");
        var logBg = Brush(_darkMode ? "#0b1214" : "#172226");
        var logText = Brush(_darkMode ? "#bfeee4" : "#d7f4ef");

        SetThemeResources(bg, surface, chrome, soft, line, text, muted, accent, selection);

        RootGrid.Background = bg;
        TopBar.Background = chrome;
        TopBar.BorderBrush = line;
        Sidebar.Background = chrome;
        Sidebar.BorderBrush = line;
        TableToolbar.BorderBrush = line;
        FieldFormBorder.Background = bg;
        FieldFormBorder.BorderBrush = line;
        HtmlTableBorder.Background = surface;
        HtmlTableBorder.BorderBrush = line;
        ValidatePanel.Background = surface;
        ValidatePanel.BorderBrush = line;
        MenuProbePanel.Background = surface;
        MenuProbePanel.BorderBrush = line;
        EngineToolsPanel.Background = soft;
        EngineToolsPanel.BorderBrush = line;
        PartToolsPanel.Background = soft;
        PartToolsPanel.BorderBrush = line;
        AppMark.Background = accent;
        ConnectionPill.Background = _darkMode ? Brush("#163323") : Brush("#eff8f3");
        ConnectionPill.BorderBrush = _darkMode ? Brush("#2d6a47") : Brush("#b9d7c7");
        ConnectionStateText.Foreground = _darkMode ? Brush("#73d99c") : Brush("#24784a");
        Foreground = text;

        foreach (var control in FindVisualChildren<Control>(this))
        {
            control.Foreground = text;
            switch (control)
            {
                case Button button:
                    button.Background = button == SaveAsButton ? accent : surface;
                    button.BorderBrush = line;
                    if (button == SaveAsButton)
                    {
                        button.Foreground = Brushes.White;
                    }
                    break;
                case TextBox textBox:
                    textBox.Background = control == LogBox ? logBg : surface;
                    textBox.BorderBrush = line;
                    textBox.CaretBrush = text;
                    if (control == LogBox)
                    {
                        textBox.Foreground = logText;
                    }
                    break;
                case ComboBox combo:
                    combo.Background = surface;
                    combo.BorderBrush = line;
                    combo.Foreground = text;
                    ApplySystemBrushOverrides(combo.Resources, surface, text, accent, selection);
                    break;
                case ToggleButton toggle:
                    toggle.Background = surface;
                    toggle.BorderBrush = line;
                    toggle.Foreground = text;
                    break;
                case ListBox list:
                    list.Background = chrome;
                    list.BorderBrush = line;
                    ApplySystemBrushOverrides(list.Resources, chrome, text, accent, selection);
                    break;
                case DataGrid grid:
                    grid.Background = surface;
                    grid.RowBackground = surface;
                    grid.AlternatingRowBackground = soft;
                    grid.BorderBrush = line;
                    grid.HorizontalGridLinesBrush = line;
                    grid.VerticalGridLinesBrush = line;
                    ApplySystemBrushOverrides(grid.Resources, surface, text, accent, selection);
                    break;
                case TabControl tab:
                    tab.Background = bg;
                    tab.BorderBrush = line;
                    break;
                case TabItem tabItem:
                    tabItem.Background = surface;
                    tabItem.BorderBrush = line;
                    tabItem.Foreground = text;
                    break;
                case ComboBoxItem comboItem:
                    comboItem.Background = surface;
                    comboItem.Foreground = text;
                    break;
                case ListBoxItem listItem:
                    listItem.Foreground = text;
                    break;
            }
        }

        StatusText.Foreground = muted;
        CarSubtitleText.Foreground = muted;
        TableStatusText.Foreground = muted;
        RoleLegendText.Foreground = muted;
        EnginePanelNote.Foreground = muted;
        PartPanelNote.Foreground = muted;
        ThemeButton.Content = _darkMode ? "Light" : "Dark";

        foreach (var card in FieldFormPanel.Children.OfType<Border>())
        {
            if (card.Child is StackPanel stack &&
                stack.Children.Count >= 2 &&
                stack.Children[0] is TextBlock label &&
                stack.Children[1] is TextBox valueBox)
            {
                ApplyFieldCardTheme(card, label, valueBox);
            }
        }

        if (HtmlTableBorder.Visibility == Visibility.Visible)
        {
            RenderHtmlEditableTable(CurrentDisplayRows());
        }
    }

    private void SetThemeResources(
        SolidColorBrush bg,
        SolidColorBrush surface,
        SolidColorBrush chrome,
        SolidColorBrush soft,
        SolidColorBrush line,
        SolidColorBrush text,
        SolidColorBrush muted,
        SolidColorBrush accent,
        SolidColorBrush selection)
    {
        Resources["AppBgBrush"] = bg;
        Resources["SurfaceBrush"] = surface;
        Resources["ChromeBrush"] = chrome;
        Resources["SurfaceSoftBrush"] = soft;
        Resources["LineBrush"] = line;
        Resources["TextBrush"] = text;
        Resources["MutedBrush"] = muted;
        Resources["AccentBrush"] = accent;
        Resources["SelectionBrush"] = selection;
        ApplySystemBrushOverrides(Resources, surface, text, accent, selection);
    }

    private static void ApplySystemBrushOverrides(ResourceDictionary resources, Brush surface, Brush text, Brush accent, Brush selection)
    {
        resources[SystemColors.WindowBrushKey] = surface;
        resources[SystemColors.ControlBrushKey] = surface;
        resources[SystemColors.ControlLightBrushKey] = surface;
        resources[SystemColors.ControlDarkBrushKey] = surface;
        resources[SystemColors.ControlTextBrushKey] = text;
        resources[SystemColors.WindowTextBrushKey] = text;
        resources[SystemColors.MenuTextBrushKey] = text;
        resources[SystemColors.GrayTextBrushKey] = text;
        resources[SystemColors.HighlightBrushKey] = selection;
        resources[SystemColors.HighlightTextBrushKey] = text;
        resources[SystemColors.InactiveSelectionHighlightBrushKey] = selection;
        resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = text;
        resources[SystemColors.HotTrackBrushKey] = accent;
    }

    private void ApplyFieldCardTheme(Border card, TextBlock? label, TextBox? valueBox)
    {
        var surface = Brush(_darkMode ? "#172124" : "#ffffff");
        var line = Brush(_darkMode ? "#2d4047" : "#d6e0e4");
        var text = Brush(_darkMode ? "#e5eff2" : "#142025");
        var muted = Brush(_darkMode ? "#98a9ae" : "#64747b");

        card.Background = surface;
        card.BorderBrush = line;
        card.BorderThickness = new Thickness(1);
        if (label is not null)
        {
            label.Foreground = muted;
        }
        if (valueBox is not null)
        {
            valueBox.Background = surface;
            valueBox.BorderBrush = line;
            valueBox.Foreground = text;
        }
    }

    private static string HeaderTextForColumn(string? tableName, string columnName)
    {
        var label = columnName.Equals("CurbWeightKg", StringComparison.OrdinalIgnoreCase)
            ? "Curb Weight kg"
            : HumanizeFieldName(columnName);
        var role = FieldValueRoles.ForColumn(tableName, columnName);
        return role.ShowBadge ? $"{label} [{role.Label}]" : label;
    }

    private string FieldLabelText(string columnName, bool isCurbWeightKg)
    {
        var label = isCurbWeightKg ? "Curb Weight kg" : HumanizeFieldName(columnName);
        var role = FieldValueRoles.ForColumn(_currentTableName, columnName);
        return role.ShowBadge ? $"{label} [{role.Label}]" : label;
    }

    private string FieldToolTip(string columnName, bool isCurbWeightKg)
    {
        var role = FieldValueRoles.ForColumn(_currentTableName, columnName);
        var detail = isCurbWeightKg
            ? "Type kg. The DB CurbWeight field stores this as kg / 100."
            : columnName;
        return role.ShowBadge
            ? $"{detail}{Environment.NewLine}{role.Label}: {role.Description}"
            : detail;
    }

    private SolidColorBrush RoleBadgeBackground(FieldValueRoleKind kind)
    {
        return kind switch
        {
            FieldValueRoleKind.Effect => Brush(_darkMode ? "#123943" : "#e9fbff"),
            FieldValueRoleKind.Base => Brush(_darkMode ? "#183528" : "#eef8f3"),
            FieldValueRoleKind.Reference => Brush(_darkMode ? "#3b2e16" : "#fff5df"),
            FieldValueRoleKind.Display => Brush(_darkMode ? "#263035" : "#eef2f4"),
            FieldValueRoleKind.Visual => Brush(_darkMode ? "#203044" : "#edf4ff"),
            FieldValueRoleKind.Menu => Brush(_darkMode ? "#302646" : "#f4efff"),
            FieldValueRoleKind.Selector => Brush(_darkMode ? "#242d34" : "#eef4f7"),
            FieldValueRoleKind.Key => Brush(_darkMode ? "#20282c" : "#f3f5f6"),
            FieldValueRoleKind.Derived => Brush(_darkMode ? "#18333b" : "#eaf8fb"),
            _ => Brush(_darkMode ? "#20282c" : "#f3f5f6")
        };
    }

    private SolidColorBrush RoleBadgeBorder(FieldValueRoleKind kind)
    {
        return kind switch
        {
            FieldValueRoleKind.Effect => Brush(_darkMode ? "#237a8a" : "#8bd8e7"),
            FieldValueRoleKind.Base => Brush(_darkMode ? "#2d6a47" : "#b9d7c7"),
            FieldValueRoleKind.Reference => Brush(_darkMode ? "#806129" : "#e6cb91"),
            FieldValueRoleKind.Display => Brush(_darkMode ? "#3c4b51" : "#d6e0e4"),
            FieldValueRoleKind.Visual => Brush(_darkMode ? "#355b87" : "#bbd3f3"),
            FieldValueRoleKind.Menu => Brush(_darkMode ? "#5a467c" : "#d3c4ef"),
            FieldValueRoleKind.Selector => Brush(_darkMode ? "#455760" : "#cddbe1"),
            FieldValueRoleKind.Key => Brush(_darkMode ? "#3c4b51" : "#d6e0e4"),
            FieldValueRoleKind.Derived => Brush(_darkMode ? "#246879" : "#a8dfe9"),
            _ => Brush(_darkMode ? "#3c4b51" : "#d6e0e4")
        };
    }

    private SolidColorBrush RoleBadgeForeground(FieldValueRoleKind kind)
    {
        return kind switch
        {
            FieldValueRoleKind.Effect => Brush(_darkMode ? "#7ee7fb" : "#057c93"),
            FieldValueRoleKind.Base => Brush(_darkMode ? "#80d9a6" : "#24784a"),
            FieldValueRoleKind.Reference => Brush(_darkMode ? "#f3c978" : "#8a6519"),
            FieldValueRoleKind.Display => Brush(_darkMode ? "#b6c8cd" : "#64747b"),
            FieldValueRoleKind.Visual => Brush(_darkMode ? "#a8ccff" : "#2f628e"),
            FieldValueRoleKind.Menu => Brush(_darkMode ? "#c7b1f0" : "#6a4a9a"),
            FieldValueRoleKind.Selector => Brush(_darkMode ? "#bed0d6" : "#556870"),
            FieldValueRoleKind.Key => Brush(_darkMode ? "#aebcc0" : "#6b797f"),
            FieldValueRoleKind.Derived => Brush(_darkMode ? "#8fe7f4" : "#27788a"),
            _ => Brush(_darkMode ? "#aebcc0" : "#6b797f")
        };
    }

    private static string HumanizeFieldName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var spaced = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2");
        spaced = spaced.Replace("_", " ");
        return spaced;
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }
            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
