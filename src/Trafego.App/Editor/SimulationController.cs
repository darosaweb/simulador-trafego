using System.Diagnostics;
using Avalonia.Threading;
using Trafego.Core.Model;
using Trafego.Core.Results;
using Trafego.Core.Serialization;
using Trafego.Sumo;

namespace Trafego.App.Editor;

public enum SimState { Stopped, Preparing, Running, Paused, Finished }

/// <summary>
/// Prepara o cenário no SUMO e avança a simulação no ritmo escolhido. O libsumo é chamado
/// sempre da thread da interface (pelo timer), como ele exige.
/// </summary>
public sealed class SimulationController
{
    /// <summary>Velocidades oferecidas; 0 = o mais rápido possível.</summary>
    public static readonly double[] Speeds = [1, 2, 5, 10, 0];

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly Stopwatch _clock = new();
    private LibsumoSession? _session;
    private double _simTarget;

    public SimState State { get; private set; } = SimState.Stopped;
    public double Speed { get; set; } = 1;
    public SumoScenario? Scenario { get; private set; }
    public SimSnapshot Last { get; private set; } = SimSnapshot.Empty;

    public event Action? StateChanged;
    public event Action<SimSnapshot>? Updated;

    public bool Active => State is not SimState.Stopped;

    public SimulationController()
    {
        _timer.Tick += (_, _) => Tick();
    }

    public async Task StartAsync(Project project, string directory)
    {
        if (State != SimState.Stopped) return;
        SetState(SimState.Preparing);
        try
        {
            // Cópia do projeto: o netconvert roda em outra thread.
            var copy = ProjectFile.FromJson(ProjectFile.ToJson(project));
            Scenario = await Task.Run(() => SumoScenario.Prepare(copy.Network, directory, copy.Simulation));
            _session = new LibsumoSession(Scenario);
        }
        catch
        {
            _session?.Dispose();
            _session = null;
            SetState(SimState.Stopped);
            throw;
        }
        _simTarget = 0;
        Last = SimSnapshot.Empty;
        _clock.Restart();
        _timer.Start();
        SetState(SimState.Running);
    }

    public void Pause()
    {
        if (State != SimState.Running) return;
        _timer.Stop();
        SetState(SimState.Paused);
    }

    public void Resume()
    {
        if (State != SimState.Paused) return;
        _clock.Restart();
        _timer.Start();
        SetState(SimState.Running);
    }

    public void Stop()
    {
        _timer.Stop();
        var simulated = _session is { } s ? s.Time - s.BeginTime : 0;
        _session?.Dispose(); // fechar grava as saídas (viagens, detectores)
        _session = null;
        Last = SimSnapshot.Empty;
        SetState(SimState.Stopped);
        Updated?.Invoke(Last);

        // Resultados da simulação animada: uma replicação, possivelmente interrompida.
        if (Scenario is { } scenario && simulated >= 60)
        {
            try
            {
                var run = ResultReader.Read(scenario, LibsumoSession.AnimationPrefix, scenario.Seed);
                ResultsReady?.Invoke(new ResultSet
                {
                    Settings = scenario.Settings,
                    Partial = simulated < scenario.EndTime - scenario.BeginTime - 1,
                    Runs = [run],
                });
            }
            catch (Exception)
            {
                // Sem resultados (saídas ausentes ou incompletas): a simulação em si correu bem.
            }
        }
    }

    /// <summary>Resultados da simulação animada, quando ela é encerrada.</summary>
    public event Action<ResultSet>? ResultsReady;

    private void Tick()
    {
        if (_session is null) return;
        var elapsed = _clock.Elapsed.TotalSeconds;
        _clock.Restart();

        int steps;
        if (Speed > 0)
        {
            // Não acumula atraso: se a máquina não acompanhar, a simulação só fica mais lenta.
            _simTarget = Math.Max(_simTarget, _session.Time) + Math.Min(elapsed, 0.25) * Speed;
            steps = (int)Math.Floor((_simTarget - _session.Time) / _session.StepLength + 1e-9);
        }
        else
        {
            steps = 0;
        }

        try
        {
            if (Speed > 0)
            {
                if (steps > 0) Last = _session.Step(steps);
            }
            else
            {
                // Máximo: avança em blocos enquanto couber em ~25 ms.
                var budget = Stopwatch.StartNew();
                do Last = _session.Step(10);
                while (budget.ElapsedMilliseconds < 25 && !Last.Finished);
            }
        }
        catch (Exception)
        {
            Stop();
            throw;
        }

        Updated?.Invoke(Last);
        if (Last.Finished)
        {
            _timer.Stop();
            SetState(SimState.Finished);
        }
    }

    private void SetState(SimState s)
    {
        State = s;
        StateChanged?.Invoke();
    }
}
