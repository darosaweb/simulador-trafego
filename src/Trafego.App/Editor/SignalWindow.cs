using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Trafego.Core.Model;

namespace Trafego.App.Editor;

/// <summary>
/// Edição dos semáforos: controladores, grupos, planos (com diagrama de tempos) e tabela de horários.
/// A janela é montada em código porque as linhas mudam com a quantidade de grupos, planos e horários.
/// Cada alteração mexe direto no modelo e chama <see cref="EditorState.Checkpoint"/>.
/// </summary>
public sealed class SignalWindow : Window
{
    private readonly EditorState _editor;
    private readonly ListBox _list = new() { MinHeight = 200 };
    private readonly ContentControl _detail = new();
    private int? _controllerId;
    private int? _planId;
    private bool _building;

    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9aa4b2"));

    public SignalWindow(EditorState editor, int? controllerId = null)
    {
        _editor = editor;
        _controllerId = controllerId;
        Title = "Semáforos — Tráfego";
        Width = 1180;
        Height = 720;
        MinWidth = 820;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var add = new Button { Content = "+ Novo semáforo" };
        add.Click += (_, _) =>
        {
            var sc = Net.AddController();
            _controllerId = sc.Id;
            _planId = null;
            Commit(rebuild: true);
        };
        var remove = new Button { Content = "Excluir" };
        remove.Click += (_, _) =>
        {
            if (Current is not { } sc) return;
            Net.RemoveController(sc.Id);
            _controllerId = Net.SignalControllers.FirstOrDefault()?.Id;
            _planId = null;
            Commit(rebuild: true);
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (_building || _list.SelectedItem is not ListBoxItem { Tag: int id }) return;
            _controllerId = id;
            _planId = null;
            Dispatcher.UIThread.Post(BuildDetail);
        };

        var left = new DockPanel { Width = 220, Margin = new Thickness(12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(remove);
        DockPanel.SetDock(buttons, Dock.Bottom);
        left.Children.Add(buttons);
        var help = new TextBlock
        {
            Text = "Para ligar um movimento a um grupo, selecione o conector no mapa e escolha o semáforo e o grupo no painel.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted,
            Margin = new Thickness(0, 8, 0, 0),
        };
        DockPanel.SetDock(help, Dock.Bottom);
        left.Children.Add(help);
        left.Children.Add(_list);

        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(new ScrollViewer
        {
            Content = _detail,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        });
        Content = root;

        _editor.Changed += OnEditorChanged;
        Closed += (_, _) => _editor.Changed -= OnEditorChanged;
        Rebuild();
    }

    private Network Net => _editor.Network;
    private SignalController? Current => _controllerId is { } id ? Net.FindController(id) : null;

    /// <summary>Desfazer/refazer troca o projeto inteiro: reconstrói para não editar objetos velhos.</summary>
    private void OnEditorChanged()
    {
        if (!_building) Rebuild();
    }

    private void Commit(bool rebuild = false)
    {
        _building = true;
        try { _editor.Checkpoint(); }
        finally { _building = false; }
        if (rebuild) Rebuild();
    }

    private void Rebuild()
    {
        _building = true;
        try
        {
            IsEnabled = !_editor.ReadOnly;
            if (Current is null) _controllerId = Net.SignalControllers.FirstOrDefault()?.Id;
            _list.Items.Clear();
            foreach (var sc in Net.SignalControllers)
            {
                var heads = Net.Connectors.Count(c => c.Signal?.ControllerId == sc.Id);
                var item = new ListBoxItem { Content = $"{sc.Name}  ({heads} cabeça(s))", Tag = sc.Id };
                _list.Items.Add(item);
                if (sc.Id == _controllerId) _list.SelectedItem = item;
            }
        }
        finally
        {
            _building = false;
        }
        BuildDetail();
    }

    // ------------------------------------------------------------------ detalhe do controlador

    private void BuildDetail()
    {
        _building = true;
        try
        {
            if (Current is not { } sc)
            {
                _detail.Content = new TextBlock
                {
                    Text = "Nenhum semáforo. Clique em \"+ Novo semáforo\".",
                    Margin = new Thickness(16),
                    Foreground = Muted,
                };
                return;
            }
            if (_planId is null || sc.FindPlan(_planId.Value) is null) _planId = sc.Plans.FirstOrDefault()?.Id;

            var panel = new StackPanel { Margin = new Thickness(8, 12, 16, 16), Spacing = 6 };
            panel.Children.Add(Row(Label("Nome", 120), Text(sc.Name, v => { sc.Name = v; Commit(rebuild: true); }, 260)));
            panel.Children.Add(Header("GRUPOS SEMAFÓRICOS"));
            panel.Children.Add(Muted_("Verde permitido: o movimento anda no verde mas cede a passagem a quem cruza (ex.: conversão à esquerda sem fase exclusiva)."));
            foreach (var g in sc.Groups) panel.Children.Add(GroupRow(sc, g));
            panel.Children.Add(Button("+ Grupo", () =>
            {
                var id = sc.NextGroupId;
                sc.Groups.Add(new SignalGroup { Id = id, Name = $"Grupo {id}" });
                foreach (var p in sc.Plans) p.Timings.Add(new GroupTiming { GroupId = id });
                Commit(rebuild: true);
            }));

            panel.Children.Add(Header("PLANOS"));
            panel.Children.Add(PlanSelector(sc));
            if (_planId is { } pid && sc.FindPlan(pid) is { } plan) panel.Children.Add(PlanEditor(sc, plan));

            panel.Children.Add(Header("TABELA DE HORÁRIOS"));
            panel.Children.Add(Muted_("A partir de cada horário vale o plano escolhido, todos os dias. Sem horários, vale o primeiro plano."));
            foreach (var s in sc.Schedule.OrderBy(s => s.TimeOfDay).ToList()) panel.Children.Add(ScheduleRow(sc, s));
            panel.Children.Add(Button("+ Horário", () =>
            {
                var last = sc.Schedule.Count > 0 ? sc.Schedule.Max(s => s.TimeOfDay) : 6 * 3600 - 3600;
                sc.Schedule.Add(new PlanSwitch { TimeOfDay = Math.Min(last + 3600, SignalController.Day - 60), PlanId = sc.Plans.FirstOrDefault()?.Id ?? 1 });
                Commit(rebuild: true);
            }));
            _detail.Content = panel;
        }
        finally
        {
            _building = false;
        }
    }

    private Control GroupRow(SignalController sc, SignalGroup g)
    {
        var heads = Net.Connectors.Where(c => c.Signal is { } s && s.ControllerId == sc.Id && s.GroupId == g.Id)
            .Select(c => c.Name).ToList();
        var permissive = new CheckBox { Content = "Verde permitido", IsChecked = g.Permissive, VerticalAlignment = VerticalAlignment.Center };
        permissive.IsCheckedChanged += (_, _) =>
        {
            if (_building) return;
            g.Permissive = permissive.IsChecked == true;
            Commit();
        };
        return Row(
            Label($"G{g.Id}", 40),
            Text(g.Name, v => { g.Name = v; Commit(rebuild: true); }, 160),
            permissive,
            new TextBlock
            {
                Text = heads.Count == 0 ? "sem cabeças" : string.Join(", ", heads),
                Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Width = 260,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            Button("×", () => { Net.RemoveGroup(sc, g.Id); Commit(rebuild: true); }));
    }

    private Control PlanSelector(SignalController sc)
    {
        var combo = new ComboBox { Width = 220 };
        foreach (var p in sc.Plans)
        {
            var item = new ComboBoxItem { Content = $"P{p.Id} · {p.Name}", Tag = p.Id };
            combo.Items.Add(item);
            if (p.Id == _planId) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (_building || combo.SelectedItem is not ComboBoxItem { Tag: int id }) return;
            _planId = id;
            Dispatcher.UIThread.Post(BuildDetail); // não refaz a tela no meio do evento da própria ComboBox
        };
        return Row(combo,
            Button("+ Plano", () =>
            {
                var id = sc.NextPlanId;
                sc.Plans.Add(new SignalPlan
                {
                    Id = id, Name = $"Plano {id}", Cycle = 90,
                    Timings = [.. sc.Groups.Select(g => new GroupTiming { GroupId = g.Id })],
                });
                _planId = id;
                Commit(rebuild: true);
            }),
            Button("Duplicar", () =>
            {
                if (_planId is not { } pid || sc.FindPlan(pid) is not { } src) return;
                var id = sc.NextPlanId;
                sc.Plans.Add(new SignalPlan
                {
                    Id = id, Name = $"{src.Name} (cópia)", Cycle = src.Cycle, Offset = src.Offset,
                    Timings = [.. src.Timings.Select(t => new GroupTiming { GroupId = t.GroupId, GreenStart = t.GreenStart, GreenEnd = t.GreenEnd, Amber = t.Amber })],
                });
                _planId = id;
                Commit(rebuild: true);
            }),
            Button("Excluir plano", () =>
            {
                if (_planId is not { } pid || sc.Plans.Count <= 1) return;
                sc.Plans.RemoveAll(p => p.Id == pid);
                sc.Schedule.RemoveAll(s => s.PlanId == pid);
                _planId = null;
                Commit(rebuild: true);
            }));
    }

    private Control PlanEditor(SignalController sc, SignalPlan plan)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        var diagrams = new List<TimingBar>();
        void Redraw()
        {
            foreach (var d in diagrams) d.InvalidateVisual();
        }

        panel.Children.Add(Row(
            Label("Nome", 120), Text(plan.Name, v => { plan.Name = v; Commit(rebuild: true); }, 200),
            Label("Ciclo (s)", 70), Number(plan.Cycle, 10, 300, 1, v => { plan.Cycle = v; Redraw(); Commit(); }),
            Label("Defasagem (s)", 100), Number(plan.Offset, 0, 300, 1, v => { plan.Offset = v; Commit(); })));

        panel.Children.Add(Row(Label("", 170), Label("Verde início", 110), Label("Verde fim", 110), Label("Amarelo", 110),
            new TextBlock { Text = "0 s → ciclo", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center }));
        foreach (var g in sc.Groups)
        {
            var t = plan.TimingOf(g.Id);
            if (t is null)
            {
                t = new GroupTiming { GroupId = g.Id };
                plan.Timings.Add(t);
            }
            var bar = new TimingBar(plan, g.Id) { Width = 300, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            diagrams.Add(bar);
            panel.Children.Add(Row(
                Label($"G{g.Id} · {g.Name}", 170),
                Number(t.GreenStart, 0, 300, 1, v => { t.GreenStart = v; Redraw(); Commit(); }),
                Number(t.GreenEnd, 0, 300, 1, v => { t.GreenEnd = v; Redraw(); Commit(); }),
                Number(t.Amber, 0, 10, 1, v => { t.Amber = v; Redraw(); Commit(); }),
                bar));
        }
        panel.Children.Add(Muted_("Tempos em segundos dentro do ciclo. Se o fim do verde for menor que o início, o verde atravessa o fim do ciclo. " +
                                  "Entre o amarelo de um grupo e o verde de um grupo conflitante, deixe alguns segundos de vermelho geral."));
        return panel;
    }

    private Control ScheduleRow(SignalController sc, PlanSwitch s)
    {
        var time = new TextBox { Text = FormatTime(s.TimeOfDay), Width = 90 };
        time.LostFocus += (_, _) =>
        {
            if (TryParseTime(time.Text, out var v))
            {
                s.TimeOfDay = v;
                Commit(rebuild: true);
            }
            else time.Text = FormatTime(s.TimeOfDay);
        };
        var combo = new ComboBox { Width = 220 };
        foreach (var p in sc.Plans)
        {
            var item = new ComboBoxItem { Content = $"P{p.Id} · {p.Name}", Tag = p.Id };
            combo.Items.Add(item);
            if (p.Id == s.PlanId) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (_building || combo.SelectedItem is not ComboBoxItem { Tag: int id }) return;
            s.PlanId = id;
            Dispatcher.UIThread.Post(() => Commit());
        };
        return Row(Label("A partir de", 90), time, combo, Button("×", () => { sc.Schedule.Remove(s); Commit(rebuild: true); }));
    }

    public static string FormatTime(double secondsOfDay)
    {
        var m = (int)Math.Round(secondsOfDay / 60);
        return $"{m / 60:00}:{m % 60:00}";
    }

    public static bool TryParseTime(string? text, out double secondsOfDay)
    {
        secondsOfDay = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':', 'h', 'H');
        if (parts.Length is < 1 or > 2) return false;
        if (!int.TryParse(parts[0], out var h)) return false;
        var m = 0;
        if (parts.Length == 2 && parts[1].Length > 0 && !int.TryParse(parts[1], out m)) return false;
        if (h is < 0 or > 23 || m is < 0 or > 59) return false;
        secondsOfDay = h * 3600 + m * 60;
        return true;
    }

    // ------------------------------------------------------------------ peças de interface

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    private static TextBlock Label(string text, double width) =>
        new() { Text = text, Width = width, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Muted, Margin = new Thickness(0, 14, 0, 2),
    };

    private static TextBlock Muted_(string text) => new() { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap };

    private static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => onClick();
        return b;
    }

    private TextBox Text(string value, Action<string> commit, double width)
    {
        var box = new TextBox { Text = value, Width = width };
        box.LostFocus += (_, _) =>
        {
            var v = box.Text?.Trim() ?? "";
            if (v.Length > 0 && v != value) commit(v);
        };
        return box;
    }

    private NumericUpDown Number(double value, double min, double max, double step, Action<double> changed)
    {
        var n = new NumericUpDown
        {
            Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step,
            FormatString = "0.#", Width = 110,
        };
        n.ValueChanged += (_, e) =>
        {
            if (_building || e.NewValue is not { } v) return;
            changed((double)v);
        };
        return n;
    }

    /// <summary>Barra do diagrama de tempos de um grupo: verde, amarelo e vermelho ao longo do ciclo.</summary>
    private sealed class TimingBar(SignalPlan plan, int groupId) : Control
    {
        private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#2ecc71"));
        private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#f1c40f"));
        private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#c0392b"));

        public override void Render(DrawingContext ctx)
        {
            var w = Bounds.Width;
            var h = Bounds.Height;
            if (plan.Cycle <= 0 || w <= 0) return;
            const int samples = 300;
            for (var i = 0; i < samples; i++)
            {
                var tc = (i + 0.5) * plan.Cycle / samples;
                var brush = plan.StateAt(groupId, tc) switch
                {
                    SignalState.Green => Green,
                    SignalState.Amber => Amber,
                    _ => Red,
                };
                ctx.FillRectangle(brush, new Rect(i * w / samples, 0, w / samples + 0.5, h));
            }
            var pen = new Pen(Brushes.Black, 1);
            for (var s = 10; s < plan.Cycle; s += 10)
            {
                var x = s / plan.Cycle * w;
                ctx.DrawLine(pen, new Point(x, h - 4), new Point(x, h));
            }
            ctx.DrawText(new FormattedText($"{plan.Cycle:0} s", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 10, Brushes.White), new Point(w - 30, 2));
        }
    }
}
