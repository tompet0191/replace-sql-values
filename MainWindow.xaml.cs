using System.Text.RegularExpressions;
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
    private SessionState? _restoredState;

    // Control-to-model bindings (populated on each Parse)
    private readonly List<(CastExpression Cast, TextBox Box)> _castControls = new();
    private readonly List<(BoolVariable Var, ComboBox Combo)> _boolVarControls = new();
    private readonly List<(TernaryExpression Ternary, RadioButton TrueRb, RadioButton FalseRb)> _complexTernaryControls = new();
    private readonly List<(GenericExpression Generic, TextBox Box)> _genericControls = new();
    private readonly List<(SqlParameter Param, TextBox Box, CheckBox QuoteBox, Border Row)> _paramControls = new();
    private readonly List<(SqlParameter Param, List<List<TextBox>> Rows, StackPanel OuterPanel, TextBox JsonBox, Func<bool> InJsonMode)> _jsonParamControls = new();

    // Alternate row colors
    private static readonly Brush RowEven = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush RowOdd  = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFA));

    public MainWindow()
    {
        InitializeComponent();

        // Restore previous session
        _restoredState = SessionPersistence.Load();
        if (_restoredState != null)
        {
            InputTextBox.Text = _restoredState.QueryText;
            EnumTextBox.Text  = _restoredState.EnumText;
            if (!string.IsNullOrWhiteSpace(_restoredState.QueryText))
                ParseQuery();
        }
        else
        {
            ShowEmptyHint();
        }

        Closing += (_, _) => SaveSession();

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
        _restoredState = null;
        ShowEmptyHint();
        SetStatus("");
        SessionPersistence.Save(new SessionState()); // wipe saved session
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

        var boolVarCount   = _parseResult.BoolVariables.Count;
        var exprCount      = _parseResult.Expressions.OfType<CastExpression>().Count()
                           + _parseResult.Expressions.OfType<GenericExpression>().Count()
                           + _parseResult.Expressions.OfType<TernaryExpression>().Count(t => t.ConditionVariable is null);
        var paramCount     = _parseResult.Parameters.Count;
        SetStatus($"Found {boolVarCount} bool variable(s), {exprCount} other expression(s), {paramCount} SQL parameter(s).");
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
        foreach (var (generic, box)        in _genericControls)  generic.ReplacementValue = box.Text;
        foreach (var (param, box, quoteBox, _) in _paramControls)
        {
            param.ReplacementValue = box.Text;
            param.QuoteValue = quoteBox.IsChecked == true;
        }

        // Serialize JSON params from the table rows or raw paste box
        foreach (var (param, rows, _, jsonBox, inJsonMode) in _jsonParamControls)
        {
            if (inJsonMode())
            {
                var raw = jsonBox.Text.Trim();
                param.ReplacementValue = string.IsNullOrEmpty(raw) ? "''" : $"'{raw}'";
            }
            else
            {
                var jsonObjects = new List<string>();
                foreach (var rowBoxes in rows)
                {
                    var fields = new List<string>();
                    for (int c = 0; c < param.JsonColumns.Count; c++)
                    {
                        var val = rowBoxes[c].Text.Trim();
                        if (string.IsNullOrEmpty(val)) continue;
                        var col = param.JsonColumns[c];
                        var jsonVal = col.IsNumeric ? val : $"\"{val.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
                        fields.Add($"\"{col.Name}\":{jsonVal}");
                    }
                    if (fields.Any())
                        jsonObjects.Add("{" + string.Join(",", fields) + "}");
                }
                param.ReplacementValue = jsonObjects.Count > 0
                    ? "'[" + string.Join(",", jsonObjects) + "]'"
                    : "''";
            }
        }

        // Resolve bool variables → ternary UseTrue
        foreach (var (bv, combo) in _boolVarControls)
        {
            bv.Value = combo.SelectedIndex switch { 0 => true, 1 => false, _ => null };
            foreach (var ternary in bv.Ternaries)
                ternary.UseTrue = bv.Value.HasValue
                    ? (bv.Value.Value ^ ternary.IsNegated)   // XOR handles negation
                    : null;
        }

        // Complex ternaries (not bound to a variable) — radio button picks
        foreach (var (ternary, trueRb, _) in _complexTernaryControls)
            ternary.UseTrue = trueRb.IsChecked == true;

        var output = _generator.Generate(
            _parseResult.CleanedQuery,
            _parseResult.Expressions,
            _parseResult.Parameters,
            KeepUnfilledParams.IsChecked == true);

        OutputTextBox.Text = output;
        SetStatus("✔ SQL generated.  Click Copy to Clipboard.");
    }

    // ──────────────────────────── Dynamic UI building ──────────────────────────────

    private void RebuildParametersPanel()
    {
        ParametersPanel.Children.Clear();
        _castControls.Clear();
        _boolVarControls.Clear();
        _complexTernaryControls.Clear();
        _genericControls.Clear();
        _paramControls.Clear();
        _jsonParamControls.Clear();

        if (_parseResult == null) return;

        var casts          = _parseResult.Expressions.OfType<CastExpression>().ToList();
        var boolVars       = _parseResult.BoolVariables;
        var complexTernaries = _parseResult.Expressions.OfType<TernaryExpression>()
                                 .Where(t => t.ConditionVariable is null).ToList();
        var generics       = _parseResult.Expressions.OfType<GenericExpression>().ToList();
        var sqlParams      = _parseResult.Parameters;

        if (casts.Any() || boolVars.Any() || complexTernaries.Any() || generics.Any())
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
            foreach (var bv in boolVars)
            {
                var (panel, combo) = MakeBoolVarRow(bv, row++);
                stack.Children.Add(panel);
                _boolVarControls.Add((bv, combo));
            }
            foreach (var ternary in complexTernaries)
            {
                var (panel, trueRb, falseRb) = MakeComplexTernaryRow(ternary, row++);
                stack.Children.Add(panel);
                _complexTernaryControls.Add((ternary, trueRb, falseRb));
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
                if (param.IsJsonParam)
                {
                    var panel = MakeJsonParamRow(param, row++);
                    stack.Children.Add(panel);
                }
                else
                {
                    var (panel, box, quoteBox, rowBorder) = MakeParamRow(param, row++);
                    stack.Children.Add(panel);
                    _paramControls.Add((param, box, quoteBox, rowBorder));
                }
            }
        }

        if (!_parseResult.Expressions.Any() && !_parseResult.Parameters.Any())
            ShowEmptyHint("No C# expressions or @parameters found in the query.");

        // Set initial param visibility based on any pre-existing ternary state
        RefreshParamVisibility();

        // Restore previously saved values if available
        RestoreValues();
        RefreshParamVisibility();
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

    private static readonly Brush BrushIncluded  = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)); // green
    private static readonly Brush BrushExcluded  = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)); // red
    private static readonly Brush BrushEmpty     = new SolidColorBrush(Color.FromRgb(0x90, 0xA4, 0xAE)); // gray
    private static readonly Brush BrushUnset     = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5)); // light gray

    private (Border panel, ComboBox combo) MakeBoolVarRow(BoolVariable bv, int index)
    {
        // Outer stack — variable name + combo on top, branch previews below
        var outer = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };

        // ── Header row: name + combo ──
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        headerGrid.Children.Add(new TextBlock
        {
            Text = bv.Name,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
            VerticalAlignment = VerticalAlignment.Center
        });

        var combo = new ComboBox
        {
            Margin = new Thickness(0, 0, 6, 0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            SelectedIndex = 2
        };
        combo.Items.Add(new ComboBoxItem { Content = "true"  });
        combo.Items.Add(new ComboBoxItem { Content = "false" });
        combo.Items.Add(new ComboBoxItem { Content = "(not set)", Foreground = Brushes.Gray });
        Grid.SetColumn(combo, 1);

        headerGrid.Children.Add(combo);
        outer.Children.Add(headerGrid);

        // ── Branch preview lines — one per ternary ──
        // For each ternary we create a TextBlock; updated live on combo change.
        var previewBlocks = new List<(TernaryExpression Ternary, TextBlock Block)>();

        foreach (var t in bv.Ternaries)
        {
            var block = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(8, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = $"true branch:  {(string.IsNullOrEmpty(t.TrueValue) ? "(empty)" : t.TrueValue)}\n" +
                          $"false branch: {(string.IsNullOrEmpty(t.FalseValue) ? "(empty)" : t.FalseValue)}"
            };
            outer.Children.Add(block);
            previewBlocks.Add((t, block));
        }

        // Helper that refreshes all preview TextBlocks for the current combo state
        void RefreshPreviews()
        {
            bool? varValue = combo.SelectedIndex switch { 0 => true, 1 => false, _ => null };

            foreach (var (t, block) in previewBlocks)
            {
                if (!varValue.HasValue)
                {
                    // Not set — show both branches dimmed
                    block.Text = $"? {(string.IsNullOrEmpty(t.TrueValue) ? "(empty)" : t.TrueValue)}"
                               + $"  /  {(string.IsNullOrEmpty(t.FalseValue) ? "(empty)" : t.FalseValue)}";
                    block.Foreground = BrushUnset;
                    block.TextDecorations = null;
                }
                else
                {
                    // Resolve: XOR for negation
                    bool useTrue = varValue.Value ^ t.IsNegated;
                    var activeText   = useTrue ? t.TrueValue  : t.FalseValue;
                    var inactiveText = useTrue ? t.FalseValue : t.TrueValue;
                    bool activeIsEmpty = string.IsNullOrEmpty(activeText);

                    if (activeIsEmpty)
                    {
                        // Active branch adds nothing — show what's being excluded in red
                        if (!string.IsNullOrEmpty(inactiveText))
                        {
                            block.Text = inactiveText;
                            block.Foreground = BrushExcluded;
                            block.TextDecorations = TextDecorations.Strikethrough;
                        }
                        else
                        {
                            block.Text = "(nothing added)";
                            block.Foreground = BrushEmpty;
                            block.TextDecorations = null;
                        }
                    }
                    else
                    {
                        block.Text = activeText;
                        block.Foreground = BrushIncluded;
                        block.TextDecorations = null;
                    }
                }
            }
        }

        combo.SelectionChanged += (_, _) =>
        {
            // Update ternary model so RefreshParamVisibility sees current state
            bool? varValue = combo.SelectedIndex switch { 0 => true, 1 => false, _ => null };
            foreach (var t in bv.Ternaries)
                t.UseTrue = varValue.HasValue ? (varValue.Value ^ t.IsNegated) : null;

            RefreshPreviews();
            RefreshParamVisibility();
        };
        RefreshPreviews(); // set initial state (no need to call RefreshParamVisibility here — params aren't built yet)

        return (WrapRow(outer, index), combo);
    }

    private (Border panel, RadioButton trueRb, RadioButton falseRb) MakeComplexTernaryRow(TernaryExpression ternary, int index)
    {
        var stack = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };

        stack.Children.Add(new TextBlock
        {
            Text = $"?  {ternary.Condition}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
            Margin = new Thickness(0, 0, 0, 4)
        });

        var group     = $"ternary_{Guid.NewGuid():N}";
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
            Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
        };
        var falseRb = new RadioButton
        {
            Content = falseText,
            GroupName = group,
            IsChecked = false,
            Margin = new Thickness(8, 1, 0, 1),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28))
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

    private (Border panel, TextBox box, CheckBox quoteBox, Border row) MakeParamRow(SqlParameter param, int index)
    {
        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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

        var quoteCheck = new CheckBox
        {
            Content = "'",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            IsChecked = !param.IsListParam,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Wrap value in single quotes when generating SQL.\nLeave unchecked for numeric values."
        };

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
                : "Raw value to substitute (without quotes — use the ' checkbox to wrap automatically)."
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(quoteCheck, 1);
        Grid.SetColumn(box, 2);
        if (!param.IsListParam)
            grid.Children.Add(label);
        grid.Children.Add(quoteCheck);
        grid.Children.Add(box);

        var row = WrapRow(grid, index);
        return (row, box, quoteCheck, row);
    }

    private Border MakeJsonParamRow(SqlParameter param, int index)
    {
        var cols = param.JsonColumns;
        var outer = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };

        // ── Header bar: @paramName [json] ──
        var headerBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        headerBar.Children.Add(new TextBlock
        {
            Text = $"@{param.Name}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x1B, 0x9A)),
            VerticalAlignment = VerticalAlignment.Center
        });
        headerBar.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xF2, 0xF1)),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(5, 0, 0, 0),
            Padding = new Thickness(4, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "json", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x69, 0x64)) }
        });
        outer.Children.Add(headerBar);

        // ── Mode toggle buttons ──
        bool jsonModeActive = false;

        var btnTable = new Button { Content = "Table",     Padding = new Thickness(10, 2, 10, 2), FontSize = 11, Margin = new Thickness(0, 0, 2, 6) };
        var btnJson  = new Button { Content = "Paste JSON", Padding = new Thickness(10, 2, 10, 2), FontSize = 11, Margin = new Thickness(0, 0, 0, 6) };

        var toggleBar = new StackPanel { Orientation = Orientation.Horizontal };
        toggleBar.Children.Add(btnTable);
        toggleBar.Children.Add(btnJson);
        outer.Children.Add(toggleBar);

        // ── Table content ──
        var tableStack = new StackPanel();

        var tableGrid = new Grid();
        for (int c = 0; c < cols.Count; c++)
            tableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ✕ column

        // Column header row
        tableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < cols.Count; c++)
        {
            var hdr = new TextBlock
            {
                Text = cols[c].Name,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4F)),
                Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF1)),
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(0, 0, 1, 2)
            };
            Grid.SetRow(hdr, 0);
            Grid.SetColumn(hdr, c);
            tableGrid.Children.Add(hdr);
        }

        var allRows = new List<List<TextBox>>();
        int nextGridRow = 1;

        void AddDataRow()
        {
            int r = nextGridRow++;
            tableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowBoxes = new List<TextBox>();

            for (int c = 0; c < cols.Count; c++)
            {
                var box = new TextBox
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(3, 1, 3, 1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5)),
                    ToolTip = $"{cols[c].Name}  ({cols[c].SqlType})"
                };
                Grid.SetRow(box, r);
                Grid.SetColumn(box, c);
                tableGrid.Children.Add(box);
                rowBoxes.Add(box);
            }
            allRows.Add(rowBoxes);

            var removeBtn = new Button
            {
                Content = "✕",
                FontSize = 10,
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(2, 0, 0, 1),
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x9A, 0x9A)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
                ToolTip = "Remove row"
            };
            Grid.SetRow(removeBtn, r);
            Grid.SetColumn(removeBtn, cols.Count);
            removeBtn.Click += (_, _) =>
            {
                foreach (var el in tableGrid.Children.OfType<UIElement>().Where(el => Grid.GetRow(el) == r).ToList())
                    el.Visibility = Visibility.Collapsed;
                allRows.Remove(rowBoxes);
            };
            tableGrid.Children.Add(removeBtn);
        }

        AddDataRow();

        tableStack.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = tableGrid,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var addRowBtn = new Button
        {
            Content = "+ Add Row",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11
        };
        addRowBtn.Click += (_, _) => AddDataRow();
        tableStack.Children.Add(addRowBtn);

        outer.Children.Add(tableStack);

        // ── JSON paste box (hidden by default) ──
        var jsonPasteBox = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 80,
            MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xBE, 0xC5)),
            Margin = new Thickness(0, 0, 0, 4),
            Visibility = Visibility.Collapsed,
            ToolTip = "Paste a JSON array here, e.g. [{\"Id\":1,\"Name\":\"foo\"}]"
        };
        outer.Children.Add(jsonPasteBox);

        // ── Active/inactive tab styling ──
        var activeTabBg   = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
        var inactiveTabBg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED));
        var activeTabFg   = Brushes.White;
        var inactiveTabFg = new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4F));

        void ApplyTabStyle(bool inJson)
        {
            btnTable.Background = inJson ? inactiveTabBg : activeTabBg;
            btnTable.Foreground = inJson ? inactiveTabFg : activeTabFg;
            btnJson.Background  = inJson ? activeTabBg : inactiveTabBg;
            btnJson.Foreground  = inJson ? activeTabFg : inactiveTabFg;
            tableStack.Visibility  = inJson ? Visibility.Collapsed : Visibility.Visible;
            jsonPasteBox.Visibility = inJson ? Visibility.Visible : Visibility.Collapsed;
        }

        ApplyTabStyle(false); // start in Table mode

        btnTable.Click += (_, _) => { jsonModeActive = false; ApplyTabStyle(false); };
        btnJson.Click  += (_, _) => { jsonModeActive = true;  ApplyTabStyle(true);  };

        _jsonParamControls.Add((param, allRows, outer, jsonPasteBox, () => jsonModeActive));
        return WrapRow(outer, index);
    }

    // ─────────────────────────── Param visibility ──────────────────────────────

    private void RefreshParamVisibility()
    {
        if (_parseResult == null) return;

        var active = GetActiveParamNames();

        foreach (var (param, box, _, rowBorder) in _paramControls)
        {
            var isActive = active.Contains(param.Name);
            rowBorder.Opacity = isActive ? 1.0 : 0.35;
            box.IsEnabled = isActive;
        }

        foreach (var (param, _, outerPanel, _, _) in _jsonParamControls)
        {
            var isActive = active.Contains(param.Name);
            if (outerPanel.Parent is Border rowBorder)
            {
                rowBorder.Opacity = isActive ? 1.0 : 0.35;
                outerPanel.IsEnabled = isActive;
            }
        }
    }

    private HashSet<string> GetActiveParamNames()
    {
        if (_parseResult == null) return new();

        // Build effective text using current ternary resolution state
        var text = _parseResult.CleanedQuery;
        foreach (var expr in _parseResult.Expressions.OfType<TernaryExpression>())
        {
            string replacement = expr.UseTrue.HasValue
                ? (expr.UseTrue.Value ? expr.TrueValue : expr.FalseValue)
                : expr.TrueValue + " " + expr.FalseValue; // unresolved → include both branches
            text = text.Replace(expr.RawExpression, replacement);
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, @"(?<!@)@([A-Za-z_]\w*)"))
            result.Add(m.Groups[1].Value);
        return result;
    }

    // ─────────────────────────── Session persistence ────────────────────────────

    private void SaveSession()
    {
        var state = new SessionState
        {
            QueryText = InputTextBox.Text,
            EnumText  = EnumTextBox.Text
        };

        foreach (var (cast, box) in _castControls)
            if (!string.IsNullOrEmpty(box.Text))
                state.CastValues[cast.Expression] = box.Text;

        foreach (var (param, box, quoteBox, _) in _paramControls)
        {
            if (!string.IsNullOrEmpty(box.Text))
                state.ParamValues[param.Name] = box.Text;
            state.ParamQuoteValues[param.Name] = quoteBox.IsChecked == true;
        }

        foreach (var (bv, combo) in _boolVarControls)
            state.BoolVariableValues[bv.Name] = combo.SelectedIndex switch { 0 => true, 1 => false, _ => null };

        foreach (var (generic, box) in _genericControls)
            if (!string.IsNullOrEmpty(box.Text))
                state.GenericValues[generic.Expression] = box.Text;

        foreach (var (ternary, trueRb, _) in _complexTernaryControls)
            state.ComplexTernaryValues[ternary.RawExpression] = trueRb.IsChecked == true;

        foreach (var (param, rows, _, jsonBox, inJsonMode) in _jsonParamControls)
        {
            state.JsonParamModes[param.Name]  = inJsonMode();
            state.JsonPasteValues[param.Name] = jsonBox.Text;
            var savedRows = new List<Dictionary<string, string>>();
            foreach (var rowBoxes in rows)
            {
                var rowDict = new Dictionary<string, string>();
                for (int c = 0; c < param.JsonColumns.Count; c++)
                    rowDict[param.JsonColumns[c].Name] = rowBoxes[c].Text;
                savedRows.Add(rowDict);
            }
            state.JsonParamRows[param.Name] = savedRows;
        }

        SessionPersistence.Save(state);
    }

    private void RestoreValues()
    {
        if (_restoredState == null) return;

        foreach (var (cast, box) in _castControls)
            if (_restoredState.CastValues.TryGetValue(cast.Expression, out var v))
                box.Text = v;

        foreach (var (param, box, quoteBox, _) in _paramControls)
        {
            if (_restoredState.ParamValues.TryGetValue(param.Name, out var v))
                box.Text = v;
            if (_restoredState.ParamQuoteValues.TryGetValue(param.Name, out var q))
                quoteBox.IsChecked = q;
        }

        foreach (var (bv, combo) in _boolVarControls)
            if (_restoredState.BoolVariableValues.TryGetValue(bv.Name, out var v))
                combo.SelectedIndex = v switch { true => 0, false => 1, _ => 2 };

        foreach (var (generic, box) in _genericControls)
            if (_restoredState.GenericValues.TryGetValue(generic.Expression, out var v))
                box.Text = v;

        foreach (var (ternary, trueRb, falseRb) in _complexTernaryControls)
            if (_restoredState.ComplexTernaryValues.TryGetValue(ternary.RawExpression, out var v))
            {
                trueRb.IsChecked  = v;
                falseRb.IsChecked = !v;
            }

        foreach (var (param, rows, _, jsonBox, _) in _jsonParamControls)
        {
            if (_restoredState.JsonPasteValues.TryGetValue(param.Name, out var paste))
                jsonBox.Text = paste;

            if (_restoredState.JsonParamRows.TryGetValue(param.Name, out var savedRows))
            {
                // First row already exists — fill it, add more as needed
                for (int r = 0; r < savedRows.Count; r++)
                {
                    if (r >= rows.Count)
                        break; // AddDataRow is not accessible here; extra rows are skipped
                    for (int c = 0; c < param.JsonColumns.Count; c++)
                        if (savedRows[r].TryGetValue(param.JsonColumns[c].Name, out var cellVal))
                            rows[r][c].Text = cellVal;
                }
            }
        }
    }

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
