using System.Text.Json;
using TriggerHelper.Config;
using TriggerHelper.Runtime;

namespace TriggerHelper.Loading;

internal sealed class ConfigLoader
{
    private const string ConfigDirectoryName = "TriggerHelper";
    private const string GlobalFileName = "global.jsonc";
    private const string MapsSubdirectory = "maps";

    public string ConfigRoot { get; }

    public ConfigLoader(string sharpPath)
    {
        ConfigRoot = Path.Combine(sharpPath, "configs", ConfigDirectoryName);
    }

    public string GlobalPath => Path.Combine(ConfigRoot, GlobalFileName);

    public string MapPath(string mapName)
        => Path.Combine(ConfigRoot, MapsSubdirectory, $"{mapName}.jsonc");

    public Result<LoadedConfig, ConfigLoadFailure> Load(string? mapName)
    {
        if (!string.IsNullOrWhiteSpace(mapName))
        {
            var mapFile = MapPath(mapName);

            if (File.Exists(mapFile))
            {
                return ReadFile(mapFile);
            }
        }

        var globalFile = GlobalPath;

        return File.Exists(globalFile)
            ? ReadFile(globalFile)
            : Result<LoadedConfig, ConfigLoadFailure>.Fail
            (
                new ConfigLoadFailure(ConfigLoadFailureKind.NotFound, globalFile, Details: null)
            );
    }

    private static Result<LoadedConfig, ConfigLoadFailure> ReadFile(string path)
    {
        GlowConfig? raw;

        try
        {
            using var stream = File.OpenRead(path);
            raw = JsonSerializer.Deserialize(stream, GlowJsonContext.Default.GlowConfig);
        }
        catch (IOException ioException)
        {
            return Result<LoadedConfig, ConfigLoadFailure>.Fail
            (
                new ConfigLoadFailure(ConfigLoadFailureKind.ReadFailed, path, ioException.Message)
            );
        }
        catch (UnauthorizedAccessException unauthorizedException)
        {
            return Result<LoadedConfig, ConfigLoadFailure>.Fail
            (
                new ConfigLoadFailure(ConfigLoadFailureKind.ReadFailed, path, unauthorizedException.Message)
            );
        }
        catch (JsonException jsonException)
        {
            var details = jsonException.LineNumber is { } line
                ? $"line {line + 1}, position {jsonException.BytePositionInLine + 1}: {jsonException.Message}"
                : jsonException.Message;

            return Result<LoadedConfig, ConfigLoadFailure>.Fail
            (
                new ConfigLoadFailure(ConfigLoadFailureKind.InvalidJson, path, details)
            );
        }

        if (raw is null)
        {
            return Result<LoadedConfig, ConfigLoadFailure>.Fail
            (
                new ConfigLoadFailure(ConfigLoadFailureKind.EmptyContent, path, Details: null)
            );
        }

        var compiled = new CompiledGlowConfig(raw);

        return Result<LoadedConfig, ConfigLoadFailure>.Ok(new LoadedConfig(compiled, path));
    }
}
