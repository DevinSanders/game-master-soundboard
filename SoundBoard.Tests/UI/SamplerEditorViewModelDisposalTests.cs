using Avalonia.Headless.XUnit;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.PluginApi;
using SoundBoard.Tests.Fakes;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.Tests.UI;

/// <summary>
/// Regression tests for the ghost-VM bug fixed in task #145. Pre-fix,
/// reopening the FX Chain editor created a new VM whose 100 ms timer
/// kept running after the editor window was swapped to a fresh VM —
/// multiple ghosts then competed to push different gain values to the
/// alive ephemerals, manifesting as randomly cutting-in/out FX on
/// per-target playbacks.
///
/// <para>These tests pin <see cref="SamplerEditorViewModel.Dispose"/>'s
/// contract. <c>[AvaloniaFact]</c> ensures each runs on the Avalonia
/// dispatcher thread — the same thread the production VM's
/// <c>DispatcherTimer</c> would tick on.</para>
/// </summary>
public class SamplerEditorViewModelDisposalTests
{
    private const string PluginId = "test.recording";

    private static (SamplerChainService chain, SamplerEditorViewModel editor, RecordingSamplerPlugin plugin, SqliteInMemoryDbFixture fx)
        BuildEditor(int presetId, float initialGain)
    {
        var fx = new SqliteInMemoryDbFixture();
        using (var db = fx.CreateContext())
        {
            // SamplerEditorViewModel.LoadAttached checks OwnerExists and
            // treats a missing owner as "this chain belongs to a deleted
            // preset", suppressing materialization. Seed a real Preset row.
            db.Presets.Add(new Preset { Id = presetId, Name = "Test Preset" });
            db.SamplerAttachments.Add(new SamplerAttachment
            {
                PluginId = PluginId,
                OwnerType = SamplerOwnerType.Preset,
                OwnerId = presetId,
                Order = 0,
                IsBypassed = false,
                ConfigJson = initialGain.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            });
            db.SaveChanges();
        }
        var plugin = new RecordingSamplerPlugin(PluginId);
        var pluginService = Substitute.For<IPluginService>();
        pluginService.LoadedPlugins.Returns(new IPlugin[] { plugin });
        var mixer = new MasterMixer();
        var chain = new SamplerChainService(fx.Factory, pluginService, mixer);

        var editor = new SamplerEditorViewModel(chain, pluginService, SamplerOwnerType.Preset, presetId, "Test Preset");
        return (chain, editor, plugin, fx);
    }

    [AvaloniaFact]
    public void Dispose_StopsAutoSaveTimer_PreventingGhostPushes()
    {
        var (chain, editor, plugin, fx) = BuildEditor(presetId: 100, initialGain: 0.3f);
        using var _ = fx;

        // Spawn an ephemeral so PushLiveConfig has a target. After this
        // call, SpawnedInstances = [editor's instance, ephemeral].
        chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100);
        var editorInner = plugin.SpawnedInstances[0];
        var ephemeral = plugin.SpawnedInstances[1];
        ephemeral.Gain.Should().BeApproximately(0.3f, 0.0001f);

        // Mutate the editor's own instance — the pre-fix ghost-push would
        // propagate THIS value into the ephemeral every 100 ms.
        editorInner.Gain = 0.7f;

        editor.Dispose();

        // Wait long enough that multiple timer ticks WOULD have fired
        // had Dispose not stopped them.
        float beforeWait = ephemeral.Gain;
        Thread.Sleep(500);

        ephemeral.Gain.Should().Be(beforeWait,
            "Dispose must stop the autosave timer; a ghost push would have set gain to the editor's 0.7");
    }

    [AvaloniaFact]
    public void Dispose_FlushesPendingPersists()
    {
        var (_, editor, plugin, fx) = BuildEditor(presetId: 200, initialGain: 0.5f);
        using var _ = fx;

        var editorInner = plugin.SpawnedInstances[0];
        editorInner.Gain = 0.25f;

        editor.Attached.Single().SchedulePersist();
        editor.Dispose();

        using var db = fx.CreateContext();
        var row = db.SamplerAttachments.First();
        row.ConfigJson.Should().Be("0.25",
            "Dispose must flush pending debounced writes — pre-fix bug was 'close mid-edit loses data'");
    }

    [AvaloniaFact]
    public void Dispose_DisposesEditorInstance()
    {
        var (_, editor, plugin, fx) = BuildEditor(presetId: 300, initialGain: 0.5f);
        using var _ = fx;

        var editorInner = plugin.SpawnedInstances[0];
        editorInner.IsDisposed.Should().BeFalse();

        editor.Dispose();

        editorInner.IsDisposed.Should().BeTrue(
            "Dispose must propagate to AttachedSamplerViewModel.DisposeEditorInstance");
    }

    [AvaloniaFact]
    public void PushLiveConfigIfChanged_NoChange_IsNoop()
    {
        // Change-driven tick: when the plugin's SerializeConfig output is
        // identical to the last push, the tick must NOT call PushLiveConfig
        // again (no DeserializeConfig fan-out, no SchedulePersist churn).
        // First call returns the new JSON, subsequent calls return null
        // until the underlying config actually changes.
        var (_, editor, plugin, fx) = BuildEditor(presetId: 500, initialGain: 0.5f);
        using var _ = fx;

        var attached = editor.Attached.Single();

        // First call: nothing pushed yet (_lastConfigJson is null), so this
        // is the initial sync — returns the current serialized value.
        var first = attached.PushLiveConfigIfChanged();
        first.Should().Be("0.5");

        // Second call with no changes: must return null and do no work.
        var second = attached.PushLiveConfigIfChanged();
        second.Should().BeNull("config didn't change — tick must be a no-op");

        // Third call after a real mutation: pushes again.
        plugin.SpawnedInstances[0].Gain = 0.9f;
        var third = attached.PushLiveConfigIfChanged();
        third.Should().Be("0.9");

        // And immediately back to no-op once we've caught up.
        var fourth = attached.PushLiveConfigIfChanged();
        fourth.Should().BeNull();
    }

    [AvaloniaFact]
    public void Dispose_IsSafeToCallTwice()
    {
        var (_, editor, _, fx) = BuildEditor(presetId: 400, initialGain: 0.5f);
        using var _ = fx;

        var act = () =>
        {
            editor.Dispose();
            editor.Dispose();
        };

        act.Should().NotThrow("double-Dispose can happen when window-close handler AND view-Unloaded both fire");
    }
}
