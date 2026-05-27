using SoundBoard.Core.Audio;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.PluginApi;
using SoundBoard.Tests.Fakes;

namespace SoundBoard.Tests.Services;

/// <summary>
/// Diagnoses the user-reported "preset FX cuts in and out" symptom. A preset
/// with multiple concurrent tracks creates one ephemeral
/// <see cref="ISamplerInstance"/> per track per attachment row. These tests
/// verify:
/// <list type="bullet">
/// <item>Each track's ephemeral has its own independent config storage.</item>
/// <item><see cref="ISamplerChainService.PushLiveConfig"/> propagates editor
///   changes to ALL alive ephemerals consistently.</item>
/// <item>Track unregistration doesn't disturb other tracks' configs.</item>
/// <item>Audio through each chain reads the most-recent gain.</item>
/// </list>
/// </summary>
public class PerTargetMultiEphemeralTests
{
    private const string PluginId = "test.recording";

    private static (SamplerChainService chain, MasterMixer mixer, RecordingSamplerPlugin plugin)
        BuildSut(SqliteInMemoryDbFixture fx)
    {
        var plugin = new RecordingSamplerPlugin(PluginId);
        var pluginService = Substitute.For<IPluginService>();
        pluginService.LoadedPlugins.Returns(new IPlugin[] { plugin });
        var mixer = new MasterMixer();
        return (new SamplerChainService(fx.Factory, pluginService, mixer), mixer, plugin);
    }

    private static int SeedPresetRow(SqliteInMemoryDbFixture fx, int presetId, float initialGain)
    {
        using var db = fx.CreateContext();
        var row = new SamplerAttachment
        {
            PluginId = PluginId,
            OwnerType = SamplerOwnerType.Preset,
            OwnerId = presetId,
            Order = 0,
            IsBypassed = false,
            ConfigJson = initialGain.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        };
        db.SamplerAttachments.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    [Fact]
    public void MultipleEphemerals_AllReceiveInitialConfigFromDb()
    {
        // Three preset tracks → BuildEphemeralChain called 3 times → 3
        // independent AttenuatorInstance equivalents. Each must pick up
        // the DB row's saved Gain on materialization. Pre-fix this would
        // have been the case anyway; the test just pins the contract.
        using var fx = new SqliteInMemoryDbFixture();
        SeedPresetRow(fx, presetId: 100, initialGain: 0.3f);
        var (chain, _, plugin) = BuildSut(fx);

        var chain1 = chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100);
        var chain2 = chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100);
        var chain3 = chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100);

        plugin.SpawnedInstances.Should().HaveCount(3, "one instance per BuildEphemeralChain call");
        plugin.SpawnedInstances.Should().AllSatisfy(i => i.Gain.Should().BeApproximately(0.3f, 0.0001f));
    }

    [Fact]
    public void PushLiveConfig_PropagatesToAllEphemerals()
    {
        using var fx = new SqliteInMemoryDbFixture();
        int rowId = SeedPresetRow(fx, presetId: 100, initialGain: 0.3f);
        var (chain, _, plugin) = BuildSut(fx);

        // Spawn 3 ephemerals (simulating 3 preset tracks).
        chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100);
        chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100);
        chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100);

        // Editor instance (separate from ephemerals).
        SamplerAttachment row;
        using (var db = fx.CreateContext()) row = db.SamplerAttachments.Find(rowId)!;
        var editorInstance = chain.CreateEditorInstance(row, out _)!;
        // User moves slider to 0.5.
        editorInstance.DeserializeConfig("0.5");

        chain.PushLiveConfig(row, editorInstance);

        // All 3 ephemerals must reflect the editor's gain.
        plugin.SpawnedInstances.Should().AllSatisfy(i => i.Gain.Should().BeApproximately(0.5f, 0.0001f));
    }

    [Fact]
    public void UnregisteringOneEphemeral_LeavesOthersUnaffected()
    {
        // Simulate one preset track finishing while two others continue.
        // The remaining two ephemerals must keep their state.
        using var fx = new SqliteInMemoryDbFixture();
        int rowId = SeedPresetRow(fx, presetId: 100, initialGain: 0.4f);
        var (chain, _, plugin) = BuildSut(fx);

        var inst1 = chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100)[0];
        var inst2 = chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100)[0];
        var inst3 = chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100)[0];

        // Move editor slider after all spawned.
        SamplerAttachment row;
        using (var db = fx.CreateContext()) row = db.SamplerAttachments.Find(rowId)!;
        var editor = chain.CreateEditorInstance(row, out _)!;
        editor.DeserializeConfig("0.7");
        chain.PushLiveConfig(row, editor);

        plugin.SpawnedInstances.Should().AllSatisfy(i => i.Gain.Should().BeApproximately(0.7f, 0.0001f));

        // Track 1 ends — unregister its ephemeral.
        chain.UnregisterEphemeral(inst1);

        // Slider moves again to 0.2.
        editor.DeserializeConfig("0.2");
        chain.PushLiveConfig(row, editor);

        // Surviving ephemerals (2, 3) must update; unregistered one (1)
        // stays at its last state (0.7) — its audio path is gone anyway.
        plugin.SpawnedInstances[0].Gain.Should().BeApproximately(0.7f, 0.0001f, "unregistered instance keeps last state");
        plugin.SpawnedInstances[1].Gain.Should().BeApproximately(0.2f, 0.0001f);
        plugin.SpawnedInstances[2].Gain.Should().BeApproximately(0.2f, 0.0001f);
    }

    [Fact]
    public void EachEphemeralChain_AppliesItsOwnGainToAudio()
    {
        // Pin the audio-path contract: each ephemeral's CreateEffect wraps
        // a source with a wet that reads from THIS ephemeral's gain. Pre-fix
        // a bug where multiple ephemerals shared state would mean updating
        // one would change all — verify they're truly independent.
        using var fx = new SqliteInMemoryDbFixture();
        SeedPresetRow(fx, presetId: 100, initialGain: 0.5f);
        var (chain, _, plugin) = BuildSut(fx);

        var instances = new[]
        {
            chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100)[0],
            chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100)[0],
            chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100)[0],
        };

        // Wrap each with a distinct source so output reveals which ephemeral
        // ran on which input.
        var sources = new[]
        {
            new CountingSampleProvider(),
            new CountingSampleProvider(),
            new CountingSampleProvider(),
        };
        var wrappedChains = instances.Select((inst, i) => inst.CreateEffect(sources[i])).ToArray();

        var buf = new float[4];
        for (int i = 0; i < 3; i++)
        {
            wrappedChains[i].Read(buf, 0, 4);
            // Initial gain = 0.5: source samples 1..4 → 0.5, 1.0, 1.5, 2.0
            buf.Should().Equal(0.5f, 1.0f, 1.5f, 2.0f);
        }
    }

    [Fact]
    public void SaveEditorInstance_PersistsAndPropagates()
    {
        // The other live-update path: SaveEditorInstance writes to DB AND
        // pushes to alive ephemerals. Must produce the same end-state as
        // PushLiveConfig + a subsequent DB-roundtrip materialization.
        using var fx = new SqliteInMemoryDbFixture();
        int rowId = SeedPresetRow(fx, presetId: 100, initialGain: 0.5f);
        var (chain, _, plugin) = BuildSut(fx);

        // Two existing ephemerals.
        chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100);
        chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100);

        SamplerAttachment row;
        using (var db = fx.CreateContext()) row = db.SamplerAttachments.Find(rowId)!;
        var editor = chain.CreateEditorInstance(row, out _)!;
        editor.DeserializeConfig("0.1");

        chain.SaveEditorInstance(row, editor);

        // Both ephemerals updated.
        plugin.SpawnedInstances.Take(2).Should().AllSatisfy(i => i.Gain.Should().BeApproximately(0.1f, 0.0001f));

        // DB row persisted.
        using var db2 = fx.CreateContext();
        var persisted = db2.SamplerAttachments.Find(rowId);
        persisted!.ConfigJson.Should().Be("0.1");

        // Now spawn a 3rd (simulating a later track add or playlist advance).
        chain.BuildEphemeralChain(SamplerOwnerType.Preset, 100);

        // New ephemeral picks up the persisted state.
        plugin.SpawnedInstances[2].Gain.Should().BeApproximately(0.1f, 0.0001f);
    }
}
