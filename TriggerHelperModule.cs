using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using TriggerHelper.Config;
using TriggerHelper.Loading;
using TriggerHelper.Runtime;

namespace TriggerHelper;

public sealed class TriggerHelperModule : IModSharpModule, IGameListener, IEntityListener, IDisposable
{
    private static class ConVar
    {
        public static class Enabled
        {
            public const string Name = "trigger_helper_enabled";
            public const string Description = "Enable the trigger-helper glow plugin (1 = on, 0 = off).";
        }

        public static class Labels
        {
            public const string Name = "trigger_helper_labels_enabled";
            public const string Description = "Render classname text labels above helper props (1 = on, 0 = off).";
        }
    }

    private static class Commands
    {
        public const string Debug = "th_debug";
        public const string Reload = "th_reload";
        public const string Permission = "admin:debug";
        public const int MaxResults = 50;
    }

    private static class HelperProp
    {
        public const string Class = "prop_dynamic_override";
        public const string Model = "models/chicken/chicken.vmdl";
        public const int OpacityPercent = 10;
    }

    private static class Label
    {
        public const string EntityClass = "point_worldtext";
        public const float VerticalOffset = 30f;
        public const float WorldUnitsPerPixel = 0.25f;
        public const string FontName = "Arial Bold";
        public const int FontSize = 30;
    }

    private const int EngineGlowType = 3;

    public string DisplayName => "Trigger Helper";

    public string DisplayAuthor => "kurumi";

    private readonly ILogger<TriggerHelperModule> _logger;
    private readonly ISharedSystem _sharedSystem;
    private readonly IModSharp _modSharp;
    private readonly IEntityManager _entityManager;
    private readonly IConVarManager _convarManager;
    private readonly ConfigLoader _configLoader;
    private readonly IConVarManager.DelegateConVarChange _convarChangeHandler;
    private readonly IConVarManager.DelegateConVarChange _labelsChangeHandler;
    private readonly string _moduleIdentity;

    private readonly Dictionary<EntityIndex, GlowAttachment> _attachments = [];

    private readonly Dictionary<EntityIndex, EntityIndex> _helperToSource = [];

    private readonly List<PendingApply> _pendingApplies = [];

    private CompiledGlowConfig? _activeConfig;
    private string? _activeSource;
    private IConVar? _enabledConVar;
    private IConVar? _labelsConVar;
    private bool _enabled = true;
    private bool _labelsEnabled = true;
    private bool _flushQueued;
    private Action? _cachedFlushDelegate;
    private Action? _cachedReloadDelegate;
    private IAdminCommandRegistry? _debugCommandRegistry;
    private ConfigWatcher? _configWatcher;
    private bool _shutdownDone;

    public TriggerHelperModule(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration coreConfiguration,
        bool hotReload
    )
    {
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<TriggerHelperModule>();
        _sharedSystem = sharedSystem;
        _modSharp = sharedSystem.GetModSharp();
        _entityManager = sharedSystem.GetEntityManager();
        _convarManager = sharedSystem.GetConVarManager();
        _configLoader = new ConfigLoader(sharpPath);
        _convarChangeHandler = OnEnabledConVarChanged;
        _labelsChangeHandler = OnLabelsConVarChanged;
        _moduleIdentity = typeof(TriggerHelperModule).Assembly.GetName().Name ?? "TriggerHelper";
    }

    public bool Init()
    {
        _enabledConVar = _convarManager.CreateConVar
        (
            ConVar.Enabled.Name,
            defaultValue: true,
            ConVar.Enabled.Description,
            ConVarFlags.Release
        );

        if (_enabledConVar is null)
        {
            _logger.LogError("Failed to create ConVar '{ConVar}'. Plugin will not load.", ConVar.Enabled.Name);

            return false;
        }

        _enabled = _enabledConVar.GetBool();

        _labelsConVar = _convarManager.CreateConVar
        (
            ConVar.Labels.Name,
            defaultValue: true,
            ConVar.Labels.Description,
            ConVarFlags.Release
        );

        if (_labelsConVar is not null)
        {
            _labelsEnabled = _labelsConVar.GetBool();
            _convarManager.InstallChangeHook(_labelsConVar, _labelsChangeHandler);
        }
        else
        {
            _logger.LogWarning("Failed to create ConVar '{ConVar}'. Labels will always render.", ConVar.Labels.Name);
        }

        _convarManager.InstallChangeHook(_enabledConVar, _convarChangeHandler);
        _modSharp.InstallGameListener(this);
        _entityManager.InstallEntityListener(this);

        TryRegisterDebugCommand();
        TryStartConfigWatcher();

        return true;
    }

    public void Shutdown()
    {
        if (_shutdownDone)
        {
            return;
        }

        _shutdownDone = true;

        _configWatcher?.Dispose();
        _configWatcher = null;

        _entityManager.RemoveEntityListener(this);
        _modSharp.RemoveGameListener(this);

        if (_enabledConVar is not null)
        {
            _convarManager.RemoveChangeHook(_enabledConVar, _convarChangeHandler);
            _enabledConVar = null;
        }

        if (_labelsConVar is not null)
        {
            _convarManager.RemoveChangeHook(_labelsConVar, _labelsChangeHandler);
            _labelsConVar = null;
        }

        ClearAllGlow();
        _pendingApplies.Clear();
        _activeConfig = null;
        _activeSource = null;
        _debugCommandRegistry = null;
    }

    public void Dispose() => Shutdown();

    int IGameListener.ListenerPriority => 0;

    int IGameListener.ListenerVersion => IGameListener.ApiVersion;

    int IEntityListener.ListenerPriority => 0;

    int IEntityListener.ListenerVersion => IEntityListener.ApiVersion;

    public void OnServerInit() => ReloadConfig();

    public void OnGameActivate()
    {
        ReloadConfig();

        if (_enabled)
        {
            ApplyToExistingEntities();
        }
    }

    public void OnGameDeactivate()
    {
        ClearAllGlow();
        _pendingApplies.Clear();
        _flushQueued = false;
    }

    public void OnEntityDeleted(IBaseEntity entity)
    {
        var index = entity.Index;

        if (_attachments.TryGetValue(index, out var attachment))
        {
            _helperToSource.Remove(attachment.SpawnedModelIndex);
            RemoveEntitySafe(attachment.SpawnedModelIndex);

            if (attachment.LabelEntityIndex is { } labelIdx)
            {
                RemoveEntitySafe(labelIdx);
            }

            _attachments.Remove(index);

            return;
        }

        if (_helperToSource.TryGetValue(index, out var sourceIdx))
        {
            // Helper prop was killed independently of the source — drop the binding and clean up
            // the orphaned label that would otherwise be left floating in the air.
            _helperToSource.Remove(index);

            if (_attachments.TryGetValue(sourceIdx, out var staleAttachment))
            {
                if (staleAttachment.LabelEntityIndex is { } orphanedLabelIdx)
                {
                    RemoveEntitySafe(orphanedLabelIdx);
                }

                _attachments.Remove(sourceIdx);
            }
        }
    }

    public void OnEntitySpawned(IBaseEntity entity)
    {
        if (!_enabled || _activeConfig is null)
        {
            return;
        }

        var classname = entity.Classname;
        var entry = _activeConfig.Match(classname);

        if (entry is null)
        {
            return;
        }

        _pendingApplies.Add(new PendingApply(entity, entity.Index, entry));

        if (_flushQueued)
        {
            return;
        }

        // One closure allocation per frame, regardless of how many entities spawn that frame.
        _flushQueued = true;
        _cachedFlushDelegate ??= FlushPendingApplies;
        _modSharp.InvokeFrameAction(_cachedFlushDelegate);
    }

    private void FlushPendingApplies()
    {
        _flushQueued = false;

        if (_activeConfig is null || !_enabled)
        {
            _pendingApplies.Clear();

            return;
        }

        for (var pendingIdx = 0; pendingIdx < _pendingApplies.Count; pendingIdx++)
        {
            var pending = _pendingApplies[pendingIdx];

            if (!pending.Entity.IsValid())
            {
                continue;
            }

            ApplyGlow(pending.Entity, pending.Index, pending.Entry);
        }

        _pendingApplies.Clear();
    }

    private void OnEnabledConVarChanged(IConVar conVar)
    {
        var newValue = conVar.GetBool();

        if (newValue == _enabled)
        {
            return;
        }

        _enabled = newValue;

        if (_enabled)
        {
            _logger.LogInformation("ConVar '{ConVar}' switched on — applying glow to live entities.",
                                   ConVar.Enabled.Name);
            ApplyToExistingEntities();
        }
        else
        {
            _logger.LogInformation("ConVar '{ConVar}' switched off — clearing existing glow.",
                                   ConVar.Enabled.Name);
            ClearAllGlow();
        }
    }

    private void OnLabelsConVarChanged(IConVar conVar)
    {
        var newValue = conVar.GetBool();

        if (newValue == _labelsEnabled)
        {
            return;
        }

        _labelsEnabled = newValue;

        if (_labelsEnabled)
        {
            _logger.LogInformation
            (
                "ConVar '{ConVar}' switched on — spawning labels on live attachments.",
                ConVar.Labels.Name
            );
            SpawnLabelsForExistingAttachments();
        }
        else
        {
            _logger.LogInformation
            (
                "ConVar '{ConVar}' switched off — clearing existing labels.",
                ConVar.Labels.Name
            );
            ClearAllLabels();
        }
    }

    private void SpawnLabelsForExistingAttachments()
    {
        if (_activeConfig is null || _attachments.Count == 0)
        {
            return;
        }

        var snapshot = _attachments.ToArray();

        foreach (var (sourceIndex, attachment) in snapshot)
        {
            if (attachment.LabelEntityIndex is not null)
            {
                continue;
            }

            var helper = _entityManager.FindEntityByIndex(attachment.SpawnedModelIndex);

            if (helper is null || !helper.IsValid())
            {
                continue;
            }

            var entry = _activeConfig.Match(attachment.Classname);

            if (entry is null)
            {
                continue;
            }

            var labelIndex = SpawnLabel(attachment.Classname, helper.GetAbsOrigin(), entry);

            if (labelIndex is not null)
            {
                _attachments[sourceIndex] = attachment with { LabelEntityIndex = labelIndex };
            }
        }
    }

    private void ClearAllLabels()
    {
        if (_attachments.Count == 0)
        {
            return;
        }

        var snapshot = _attachments.ToArray();

        foreach (var (sourceIndex, attachment) in snapshot)
        {
            if (attachment.LabelEntityIndex is not { } labelIdx)
            {
                continue;
            }

            RemoveEntitySafe(labelIdx);
            _attachments[sourceIndex] = attachment with { LabelEntityIndex = null };
        }
    }

    private void ReloadConfig()
    {
        var mapName = _modSharp.GetMapName();
        var result = _configLoader.Load(mapName);

        if (result.TryGetValue(out var loaded))
        {
            _activeConfig = loaded.Config;
            _activeSource = loaded.Source;

            _logger.LogInformation
            (
                "Loaded trigger-helper config from '{Source}' ({EntityCount} entries / {ExcludeCount} exclude patterns).",
                _activeSource,
                _activeConfig.EntityCount,
                _activeConfig.ExcludePatternCount
            );

            return;
        }

        _activeConfig = null;
        _activeSource = null;

        if (!result.TryGetError(out var failure))
        {
            return;
        }

        switch (failure.Kind)
        {
            case ConfigLoadFailureKind.NotFound:
                _logger.LogInformation
                (
                    "No config found for map '{Map}' (checked '{MapPath}', then global '{GlobalPath}').",
                    mapName ?? "<unknown>",
                    mapName is null ? "<n/a>" : _configLoader.MapPath(mapName),
                    _configLoader.GlobalPath
                );

                break;

            case ConfigLoadFailureKind.InvalidJson:
                _logger.LogError
                (
                    "Config '{Path}' has invalid JSON: {Details}",
                    failure.Path,
                    failure.Details
                );

                break;

            case ConfigLoadFailureKind.ReadFailed:
                _logger.LogError
                (
                    "Could not read config '{Path}': {Details}",
                    failure.Path,
                    failure.Details
                );

                break;

            case ConfigLoadFailureKind.EmptyContent:
                _logger.LogError
                (
                    "Config '{Path}' parsed but was empty/null — fix or delete the file.",
                    failure.Path
                );

                break;

            default:
                _logger.LogError("Unknown config-load failure ({Kind}) for '{Path}'.", failure.Kind, failure.Path);

                break;
        }
    }

    private void ApplyToExistingEntities()
    {
        var config = _activeConfig;

        if (config is null)
        {
            return;
        }

        var maxEntities = _modSharp.GetGlobals().MaxEntities;
        var applied = 0;

        for (EntityIndex entityIndex = 0; entityIndex <= maxEntities; entityIndex++)
        {
            var entity = _entityManager.FindEntityByIndex(entityIndex);

            if (entity is null || !entity.IsValid())
            {
                continue;
            }

            var classname = entity.Classname;
            var entry = config.Match(classname);

            if (entry is null)
            {
                continue;
            }

            if (ApplyGlow(entity, entity.Index, entry))
            {
                applied++;
            }
        }

        if (applied > 0)
        {
            _logger.LogInformation("Applied glow to {Count} existing entities.", applied);
        }
    }

    private bool ApplyGlow(IBaseEntity source, EntityIndex sourceIndex, CompiledGlowEntry entry)
    {
        if (_attachments.ContainsKey(sourceIndex))
        {
            return false;
        }

        if (!PassesSizeFilter(source, entry, out var worldCenter, out _))
        {
            return false;
        }

        var keyValues = new Dictionary<string, KeyValuesVariantValueItem>(StringComparer.Ordinal)
        {
            ["model"] = HelperProp.Model,
            ["origin"] = $"{worldCenter.X} {worldCenter.Y} {worldCenter.Z}",
            ["solid"] = 0,
            ["disableshadows"] = true,
            ["spawnflags"] = 0,
        };

        IBaseModelEntity? helper;

        try
        {
            helper = _entityManager.SpawnEntitySync<IBaseModelEntity>(HelperProp.Class, keyValues);
        }
        catch (Exception ex)
        {
            _logger.LogWarning
            (
                "Failed to spawn helper prop for '{Classname}' (model '{Model}'): {Error}",
                source.Classname, HelperProp.Model, ex.Message
            );

            return false;
        }

        if (helper is null || !helper.IsValid())
        {
            _logger.LogWarning
            (
                "Helper prop spawn returned null for '{Classname}' (model '{Model}' — ensure it exists in your content set).",
                source.Classname, HelperProp.Model
            );

            return false;
        }

        // Tint the prop's body with the configured glow color and crank alpha down so the helper
        // reads as a faint ghost outlined by the rim-lit glow. RenderMode.TransAlpha is required —
        // without it m_clrRender's alpha channel is ignored and the prop renders fully opaque.
        var bodyAlpha = (byte)(HelperProp.OpacityPercent * 255 / 100);
        helper.RenderMode = RenderMode.TransAlpha;
        helper.RenderColor = new Color32(entry.Color.R, entry.Color.G, entry.Color.B, bodyAlpha);

        WriteGlow(helper.GetGlowProperty(), entry, flashing: entry.Mode == GlowMode.Pulse);

        var helperIndex = helper.Index;
        var labelIndex = _labelsEnabled
            ? SpawnLabel(source.Classname, worldCenter, entry)
            : null;

        _attachments[sourceIndex] = new GlowAttachment(entry.Mode, source.Classname, helperIndex, labelIndex);
        _helperToSource[helperIndex] = sourceIndex;

        return true;
    }

    private EntityIndex? SpawnLabel(string classname, Vector helperCenter, CompiledGlowEntry entry)
    {
        var labelOrigin = new Vector(helperCenter.X, helperCenter.Y, helperCenter.Z + Label.VerticalOffset);
        var color = entry.Color;

        var keyValues = new Dictionary<string, KeyValuesVariantValueItem>(StringComparer.Ordinal)
        {
            ["origin"] = $"{labelOrigin.X} {labelOrigin.Y} {labelOrigin.Z}",
            ["message"] = classname,
            ["enabled"] = 1,
            ["fullbright"] = 1,
            ["world_units_per_pixel"] = Label.WorldUnitsPerPixel,
            ["font_size"] = Label.FontSize,
            ["font_name"] = Label.FontName,
            ["justify_horizontal"] = 1,                       // 0 left, 1 center, 2 right
            ["justify_vertical"] = 1,                         // 0 top, 1 center, 2 bottom
            ["reorient_mode"] = 1,                            // 1 = billboard around up-axis
            ["depth_render_offset"] = 0.1f,                   // nudge forward to avoid z-fighting
            ["color"] = $"{color.R} {color.G} {color.B} 255",
        };

        IWorldText? label;

        try
        {
            label = _entityManager.SpawnEntitySync<IWorldText>(Label.EntityClass, keyValues);
        }
        catch (Exception ex)
        {
            _logger.LogWarning
            (
                "Failed to spawn label for '{Classname}': {Error}",
                classname, ex.Message
            );

            return null;
        }

        if (label is null || !label.IsValid())
        {
            return null;
        }

        // CPointWorldText derives from CBaseModelEntity in CS2, so the glow property is real —
        // applying it lets the text shine through walls just like the chicken does. Mirror the
        // chicken's flashing flag so pulse-mode labels also strobe.
        try
        {
            WriteGlow(label.GetGlowProperty(), entry, flashing: entry.Mode == GlowMode.Pulse);
        }
        catch (Exception ex)
        {
            _logger.LogWarning
            (
                "Could not glow label for '{Classname}': {Error}",
                classname, ex.Message
            );
        }

        return label.Index;
    }

    private static void WriteGlow(IGlowProperty glow, CompiledGlowEntry entry, bool flashing)
    {
        glow.GlowType = EngineGlowType;
        glow.GlowRangeMin = entry.MinGlowRange;
        glow.GlowRangeMax = entry.MaxGlowRange;
        glow.Flashing = flashing;
        glow.EligibleForScreenHighlight = true;
        glow.GlowColorOverride = entry.Color;
        glow.GlowColor = entry.ColorVector;
        glow.Glowing = true;
    }

    private static bool PassesSizeFilter(IBaseEntity entity,
        CompiledGlowEntry entry,
        out Vector worldCenter,
        out Vector extents)
    {
        worldCenter = entity.GetAbsOrigin();
        extents = new Vector(0f, 0f, 0f);

        var collision = entity.GetCollisionProperty();

        if (collision is null)
        {
            // No collision component → no bbox. Apply if neither size bound is set; otherwise
            // skip — we can't honor a size filter without dimensions.
            return entry is { MinSize: <= 0f, MaxSize: <= 0f };
        }

        var mins = collision.Mins;
        var maxs = collision.Maxs;

        extents = new Vector(maxs.X - mins.X, maxs.Y - mins.Y, maxs.Z - mins.Z);
        worldCenter = new Vector
        (
            worldCenter.X + (mins.X + maxs.X) * 0.5f,
            worldCenter.Y + (mins.Y + maxs.Y) * 0.5f,
            worldCenter.Z + (mins.Z + maxs.Z) * 0.5f
        );

        var size = extents.X + extents.Y;

        if (entry.MinSize > 0f && size < entry.MinSize)
        {
            return false;
        }

        if (entry.MaxSize > 0f && size > entry.MaxSize)
        {
            return false;
        }

        return true;
    }

    private void ClearAllGlow()
    {
        if (_attachments.Count == 0)
        {
            return;
        }

        // Snapshot because OnEntityDeleted (via RemoveEntitySafe) may fire synchronously and
        // mutate the dictionary mid-iteration.
        var snapshot = _attachments.ToArray();

        foreach (var (_, attachment) in snapshot)
        {
            RemoveEntitySafe(attachment.SpawnedModelIndex);

            if (attachment.LabelEntityIndex is { } labelIdx)
            {
                RemoveEntitySafe(labelIdx);
            }
        }

        _attachments.Clear();
        _helperToSource.Clear();
    }

    private void RemoveEntitySafe(EntityIndex helperIndex)
    {
        var helper = _entityManager.FindEntityByIndex(helperIndex);

        if (helper is null || !helper.IsValid())
        {
            return;
        }

        // Kill is the canonical "destroy this entity" engine input — equivalent to ent_remove.
        helper.AcceptInput("Kill");
    }

    private void TryRegisterDebugCommand()
    {
        var moduleInterface = _sharedSystem.GetSharpModuleManager()
                                           .GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity);

        if (moduleInterface?.Instance is not { } adminManager)
        {
            _logger.LogInformation("AdminManager not loaded; '{Command}' debug command will be unavailable.", Commands.Debug);

            return;
        }

        try
        {
            _debugCommandRegistry = adminManager.GetCommandRegistry(_moduleIdentity);
            _debugCommandRegistry.RegisterPermissions([Commands.Permission]);
            _debugCommandRegistry.RegisterAdminCommand(Commands.Debug, OnDebugCommand, [Commands.Permission]);
            _debugCommandRegistry.RegisterAdminCommand(Commands.Reload, OnReloadCommand, [Commands.Permission]);

            _logger.LogInformation
            (
                "Registered admin commands '{DebugCmd}' and '{ReloadCmd}' (permission '{Permission}').",
                Commands.Debug, Commands.Reload, Commands.Permission
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not register admin commands: {Error}", ex.Message);
            _debugCommandRegistry = null;
        }
    }

    private void TryStartConfigWatcher()
    {
        var directory = _configLoader.ConfigRoot;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning
            (
                "Could not ensure config directory '{Directory}' exists; live reload disabled: {Error}",
                directory, ex.Message
            );

            return;
        }

        try
        {
            _configWatcher = new ConfigWatcher(directory, OnConfigFileChanged, _logger);

            _logger.LogInformation("Watching '{Directory}' for live config reloads.", directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning
            (
                "Could not start config watcher on '{Directory}'; live reload disabled: {Error}",
                directory, ex.Message
            );

            _configWatcher = null;
        }
    }

    private void OnConfigFileChanged()
    {
        _cachedReloadDelegate ??= () => ReloadConfigLive("file-change");
        _modSharp.InvokeFrameAction(_cachedReloadDelegate);
    }

    private void OnReloadCommand(IGameClient? caller, StringCommand command)
    {
        var who = caller?.Name ?? "server-console";

        ReloadConfigLive($"command by {who}");

        var status = _activeConfig is null
            ? "[trigger-helper] reload finished — no active config (see logs)."
            : $"[trigger-helper] reload finished — {_activeConfig.EntityCount} entries from '{_activeSource}'.";

        EmitLines(caller, [status]);
    }

    private void ReloadConfigLive(string trigger)
    {
        _logger.LogInformation("Reloading config ({Trigger}).", trigger);

        ClearAllGlow();
        _pendingApplies.Clear();
        _flushQueued = false;

        ReloadConfig();

        if (_enabled && _activeConfig is not null)
        {
            ApplyToExistingEntities();
        }
    }

    private void OnDebugCommand(IGameClient? caller, StringCommand command)
    {
        var pattern = command.ArgCount > 0
            ? command[1].Trim()
            : "*";

        if (string.IsNullOrEmpty(pattern))
        {
            pattern = "*";
        }

        var radius = command.ArgCount > 1
            ? command.TryGet<float?>(2) ?? 0f
            : 0f;

        if (radius < 0f)
        {
            radius = 0f;
        }

        Vector? playerOrigin = null;

        if (radius > 0f)
        {
            playerOrigin = caller?.GetPlayerController()?.GetPlayerPawn()?.GetAbsOrigin();

            if (playerOrigin is null)
            {
                // Server console or no live pawn — silently fall back to map-wide scan rather
                // than refusing to produce any output.
                radius = 0f;
            }
        }

        _logger.LogInformation(
            "th_debug args: argCount={ArgCount} pattern='{Pattern}' radius={Radius} caller={Caller}",
            command.ArgCount, pattern, radius, caller?.Name ?? "<server>");

        EmitLines(caller, BuildEntityReport(pattern, radius, playerOrigin));
    }

    private List<string> BuildEntityReport(string pattern, float radius, Vector? playerOrigin)
    {
        var patternSpan = pattern.AsSpan();
        var radiusSq = radius * radius;
        var maxEntities = _modSharp.GetGlobals().MaxEntities;

        var rows = new List<DebugRow>(capacity: Commands.MaxResults);
        var totalMatches = 0;
        var visited = 0;
        var nullSlots = 0;
        var invalidSlots = 0;
        var unmatched = 0;

        for (EntityIndex idx = 0; idx <= maxEntities; idx++)
        {
            visited++;

            IBaseEntity? entity;

            try
            {
                entity = _entityManager.FindEntityByIndex(idx);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("th_debug: FindEntityByIndex({Idx}) threw: {Error}", (int)idx, ex.Message);

                continue;
            }

            if (entity is null)
            {
                nullSlots++;

                continue;
            }

            if (!entity.IsValid())
            {
                invalidSlots++;

                continue;
            }

            string classname;

            try
            {
                classname = entity.Classname;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("th_debug: entity #{Idx} Classname threw: {Error}", (int)idx, ex.Message);

                continue;
            }

            if (!Wildcard.IsMatch(classname.AsSpan(), patternSpan))
            {
                unmatched++;

                continue;
            }

            var origin = entity.GetAbsOrigin();
            var distance = 0f;

            if (playerOrigin is { } p)
            {
                var dx = origin.X - p.X;
                var dy = origin.Y - p.Y;
                var dz = origin.Z - p.Z;
                var distSq = dx * dx + dy * dy + dz * dz;

                if (distSq > radiusSq)
                {
                    continue;
                }

                distance = MathF.Sqrt(distSq);
            }

            totalMatches++;

            if (rows.Count >= Commands.MaxResults)
            {
                continue;
            }

            var extents = entity.GetCollisionProperty() is { } col
                ? new Vector(col.Maxs.X - col.Mins.X, col.Maxs.Y - col.Mins.Y, col.Maxs.Z - col.Mins.Z)
                : default;

            rows.Add(new DebugRow(idx, classname, origin, extents, distance));
        }

        _logger.LogInformation(
            "th_debug scan: maxEntities={Max} visited={Visited} null={Null} invalid={Invalid} unmatched={Unmatched} matched={Matched}",
            maxEntities, visited, nullSlots, invalidSlots, unmatched, totalMatches);

        var scopeDesc = playerOrigin is null
            ? "on map"
            : $"within {radius:F0}u of player";

        var nameDesc = pattern == "*" ? string.Empty : $" matching '{pattern}'";

        var lines = new List<string>
        {
            $"[trigger-helper] {totalMatches} entities{nameDesc} {scopeDesc}",
        };

        foreach (var row in rows)
        {
            var distPart = playerOrigin is null
                ? string.Empty
                : $" dist={row.Distance:F0}";

            lines.Add
            (
                $"  #{(int)row.Index} {row.Classname}{distPart} " +
                $"origin=({row.Origin.X:F0} {row.Origin.Y:F0} {row.Origin.Z:F0}) " +
                $"size=({row.Extents.X:F0} {row.Extents.Y:F0} {row.Extents.Z:F0})"
            );
        }

        if (totalMatches > rows.Count)
        {
            lines.Add($"  …and {totalMatches - rows.Count} more (capped at {Commands.MaxResults})");
        }

        return lines;
    }

    private void EmitLines(IGameClient? caller, IReadOnlyList<string> lines)
    {
        if (caller is null)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                _logger.LogInformation("{Line}", lines[i]);
            }

            return;
        }

        // IPlayerController.Print(Console) is the route every sibling plugin (AdminCommands,
        // MenuManager, …) uses for client-facing output. IGameClient.ConsolePrint exists but in
        // practice drops messages silently on high-volume sends — observed with 50+ lines of
        // th_debug output. Fall back to ConsolePrint only when no live controller is reachable
        // (spectator/connecting edge cases).
        var controller = caller.GetPlayerController();

        if (controller is null)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                caller.ConsolePrint(lines[i]);
            }

            return;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            controller.Print(HudPrintChannel.Console, lines[i]);
        }
    }

    private readonly record struct DebugRow(EntityIndex Index, string Classname, Vector Origin, Vector Extents, float Distance);

    private readonly record struct PendingApply(IBaseEntity Entity, EntityIndex Index, CompiledGlowEntry Entry);
}
