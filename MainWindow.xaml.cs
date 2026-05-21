using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReplaceValuesSql.Models;
using ReplaceValuesSql.Services;

namespace ReplaceValuesSql;

public partial class MainWindow : Window
{
    private readonly QueryParser _parser = new();
    private readonly QueryGenerator _generator = new();

    private ParseResult? _parseResult;

    // Control-to-model bindings (populated on each Parse)
    private readonly List<(CastExpression Cast, TextBox Box)> _castControls = new();
    private readonly List<(TernaryExpression Ternary, RadioButton TrueRb, RadioButton FalseRb)> _ternaryControls = new();
    private readonly List<(GenericExpression Generic, TextBox Box)> _genericControls = new();
    private readonly List<(SqlParameter Param, TextBox Box)> _paramControls = new();

    // Alternate row colors
    private static readonly Brush RowEven = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush RowOdd  = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFA));

    public MainWindow()
    {
        InitializeComponent();
        ShowEmptyHint();

        // Keyboard shortcuts
        InputBinding parseShortcut = new KeyBinding(
            new RelayCommand(_ => ParseQuery()),
            new KeyGesture(Key.Enter, ModifierKeys.Control));
        InputBinding generateShortcut = new KeyBinding(
            new RelayCommand(_ => GenerateSql()),
            new KeyGesture(Key.Enter, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(parseShortcut);
        InputBindings.Add(generateShortcut);
    }

    // ─────────────────────────────── Button handlers ───────────────────────────────

    private void ParseButton_Click(object sender, RoutedEventArgs e) => ParseQuery();

    private void ApplyEnumsButton_Click(object sender, RoutedEventArgs e) => ApplyEnums();

    private void GenerateButton_Click(object sender, RoutedEventArgs e) => GenerateSql();

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OutputTextBox.Text)) return;
        Clipboard.SetText(OutputTextBox.Text);
        SetStatus("✔ Copied to clipboard!");
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        InputTextBox.Clear();
        EnumTextBox.Clear();
        OutputTextBox.Clear();
        _parseResult = null;
        ShowEmptyHint();
        SetStatus("");
    }

    // ─────────────────────────────── Core logic ────────────────────────────────────

    private void ParseQuery()
    {
        var input = InputTextBox.Text;
        if (string.IsNullOrWhiteSpace(input))
        {
            MessageBox.Show("Paste a SQL query first.", "Nothing to parse",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _parseResult = _parser.Parse(input);
        RebuildParametersPanel();

        // Auto-apply enums if already filled in
        if (!string.IsNullOrWhiteSpace(EnumTextBox.Text))
            ApplyEnums(silent: true);

        var exprCount  = _parseResult.Expressions.Count;
        var paramCount = _parseResult.Parameters.Count;
        SetStatus($"Found {exprCount} C# expression(s) and {paramCount} SQL parameter(s).  Fill values and click Generate SQL.");
    }

    private void ApplyEnums(bool silent = false)
    {
        if (_parseResult == null || _castControls.Count == 0)
        {
            if (!silent)
                SetStatus("Parse a query first, then apply enums.");
            return;
        }

        var enumValues = QueryParser.ParseEnumValues(EnumTextBox.Text);
        int applied = 0;

        foreach (var (cast, box) in _castControls)
        {
            if (enumValues.TryGetValue(cast.MemberName, out var val))
            {
                box.Text = val;
                cast.ReplacementValue = val;
                applied++;
            }
        }

        SetStatus(applied > 0
            ? $"✔ Auto-filled {applied} cast expression(s) from enum definitions."
            : "No matching enum members found — check that member names match.");
    }

    private void GenerateSql()
    {
        if (_parseResult == null)
        {
            MessageBox.Show("Parse a query first.", "No query",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Read values from controls back into the model
        foreach (var (cast, box)           in _castControls)     cast.ReplacementValue = box.Text;
        foreach (var (ternary, trueRb, _)  in _ternaryControls)  ternary.UseTrue = trueRb.IsChecked == true;
        foreach (var (generic, box)        in _genericControls)  generic.ReplacementValue = box.Text;
        foreach (var (param, box)          in _paramControls)    param.ReplacementValue = box.Text;

        var output = _generator.Generate(
            _parseResult.CleanedQuery,
            _parseResult.Expressions,
            _parseResult.Parameters,
            KeepUnfilledParams.IsChecked == true);

        OutputTextBox.Text = output;
        SetStatus("✔ SQL generated.  Click Copy to Clipboard to use in SSMS.");
    }

    // ──────────────────────────── Dynamic UI building ──────────────────────────────

    private void RebuildParametersPanel()
    {
        ParametersPanel.Children.Clear();
        _castControls.Clear();
        _ternaryControls.Clear();
        _genericControls.Clear();
        _paramControls.Clear();

        if (_parseResult == null) return;

        var casts     = _parseResult.Expressions.OfType<CastExpression>().ToList();
        var ternaries = _parseResult.Expressions.OfType<TernaryExpression>().ToList();
        var generics  = _parseResult.Expressions.OfType<GenericExpression>().ToList();
        var sqlParams = _parseResult.Parameters;

        if (casts.Any() || ternaries.Any() || generics.Any())
        {
            var expander = MakeExpander("⚙  C# Expressions");
            var stack    = new StackPanel();
            expander.Content = stack;
            ParametersPanel.Children.Add(expander);

            int row = 0;
            foreach (var cast in casts)
            {
                var (panel, box) = MakeCastRow(cast, row++);
                stack.Children.Add(panel);
                _castControls.Add((cast, box));
            }
            foreach (var ternary in ternaries)
            {
                var (panel, trueRb, falseRb) = MakeTernaryRow(ternary, row++);
                stack.Children.Add(panel);
                _ternaryControls.Add((ternary, trueRb, falseRb));
            }
            foreach (var generic in generics)
            {
                var (panel, box) = MakeGenericRow(generic, row++);
                stack.Children.Add(panel);
                _genericControls.Add((generic, box));
            }
        }

        if (sqlParams.Any())
        {
            var expander = MakeExpander("@  SQL Parameters");
            var stack    = new StackPanel();
            expander.Content = stack;
            ParametersPanel.Children.Add(expander);

            int row = 0;
            foreach (var param in sqlParams)
            {
                var (panel, box) = MakeParamRow(param, row++);
                stack.Children.Add(panel);
                _paramControls.Add((param, box));
            }
        }

        if (!_parseResult.Expressions.Any() && !_parseResult.Parameters.Any())
            ShowEmptyHint("No C# expressions or @parameters found in the query.");
    }

    // ─────────────────────────────── Row factories ─────────────────────────────────

    private (Border panel, TextBox box) MakeCastRow(CastExpression cast, int index)
    {
        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = $"({cast.CastType}){cast.ValuePath}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 8, 0),
            ToolTip = cast.Expression
        };

        var box = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 2, 6, 2),
            Padding = new Thickness(4, 2, 4, 2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5)),
            Text = cast.ReplacementValue ?? "",
            ToolTip = "Integer value for this enum member"
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(box, 1);
        grid.Children.Add(label);
        grid.Children.Add(box);

        return (WrapRow(grid, index), box);
    }

    private (Border panel, RadioButton trueRb, RadioButton falseRb) MakeTernaryRow(TernaryExpression ternary, int index)
    {
        var stack = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };

        var condLabel = new TextBlock
        {
            Text = $"?  {ternary.Condition}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
            Margin = new Thickness(0, 0, 0, 4)
        };
        stack.Children.Add(condLabel);

        var group = $"ternary_{Guid.NewGuid():N}";

        var trueText  = string.IsNullOrEmpty(ternary.TrueValue)  ? "(empty string)" : ternary.TrueValue;
        var falseText = string.IsNullOrEmpty(ternary.FalseValue) ? "(empty string)" : ternary.FalseValue;

        var trueRb = new RadioButton
        {
            Content = trueText,
            GroupName = group,
            IsChecked = true,
            Margin = new Thickness(8, 1, 0, 1),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
            ToolTip = "Include this SQL fragment"
        };
        var falseRb = new RadioButton
        {
            Content = falseText,
            GroupName = group,
            IsChecked = false,
            Margin = new Thickness(8, 1, 0, 1),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
            ToolTip = "Use this branch instead"
        };

        stack.Children.Add(trueRb);
        stack.Children.Add(falseRb);

        return (WrapRow(stack, index), trueRb, falseRb);
    }

    private (Border panel, TextBox box) MakeGenericRow(GenericExpression generic, int index)
    {
        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var display = generic.Expression.Length > 45
            ? generic.Expression[..42] + "…"
            : generic.Expression;

        var label = new TextBlock
        {
            Text = display,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x14, 0x8C)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 8, 0),
            ToolTip = generic.Expression
        };

        var box = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 2, 6, 2),
            Padding = new Thickness(4, 2, 4, 2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5)),
            Text = generic.ReplacementValue ?? ""
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(box, 1);
        grid.Children.Add(label);
        grid.Children.Add(box);

        return (WrapRow(grid, index), box);
    }

    private (Border panel, TextBox box) MakeParamRow(SqlParameter param, int index)
    {
        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x1B, 0x9A)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 8, 0)
        };

        if (param.IsListParam)
        {
            // Inline: "@CompanyIds" + small [list] badge
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = $"@{param.Name}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x1B, 0x9A)),
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF6)),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(5, 0, 0, 0),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "list",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0x51, 0xB5))
                }
            });
            label.Visibility = Visibility.Collapsed; // hide the plain label
            Grid.SetColumn(sp, 0);
            grid.Children.Add(sp);
        }
        else
        {
            label.Text = $"@{param.Name}";
        }

        var box = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 2, 6, 2),
            Padding = new Thickness(4, 2, 4, 2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5)),
            Text = param.ReplacementValue ?? "",
            ToolTip = param.IsListParam
                ? "IN-list value — enter as  1,2,3  or  (1,2,3)  (parentheses added automatically)"
                : "Value to substitute inline.\n• Number:  123\n• String:  'hello'\n• IN list: (1, 2, 3)"
        };

        if (!param.IsListParam)
            Grid.SetColumn(label, 0);

        Grid.SetColumn(box, 1);
        if (!param.IsListParam)
            grid.Children.Add(label);
        grid.Children.Add(box);

        return (WrapRow(grid, index), box);
    }

    // ─────────────────────────────── Helpers ───────────────────────────────────────

    private static Border WrapRow(UIElement content, int index) =>
        new()
        {
            Background = index % 2 == 0 ? RowEven : RowOdd,
            Padding = new Thickness(0, 3, 0, 3),
            Child = content
        };

    private static Expander MakeExpander(string header) =>
        new()
        {
            Header = header,
            IsExpanded = true,
            Margin = new Thickness(0, 6, 0, 2),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4F))
        };

    private void ShowEmptyHint(string message = "Paste a query on the left and click  Parse Query.")
    {
        ParametersPanel.Children.Clear();
        ParametersPanel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Brushes.Gray,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(6),
            TextWrapping = TextWrapping.Wrap
        });
    }

    private void SetStatus(string text) => StatusText.Text = text;
}

// Minimal ICommand for keyboard shortcuts
file sealed class RelayCommand(Action<object?> execute) : ICommand
{
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => execute(p);
}
