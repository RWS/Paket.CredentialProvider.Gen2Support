using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NLog;
using NLog.Config;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Protocol.Plugins;

static void WriteCredentialResponse(string username = "", string password = "", string message = "")
{
    var jsonOptions = new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    Console.Write(JsonSerializer.Serialize(new
    {
        Username = username,
        Password = password,
        Message = message
    }, jsonOptions));
}

static bool HasFlag(string[] argv, string flag)
{
    string lowerFlag = flag.ToLowerInvariant();
    return argv.Any(arg =>
    {
        string lowerArg = arg.ToLowerInvariant();
        return lowerArg == lowerFlag
            || lowerArg == $"/{lowerFlag}"
            || lowerArg == $"-{lowerFlag}"
            || lowerArg == $"--{lowerFlag}";
    });
}

static Logger ConfigureLogging()
{
    const string configFileName = "NLog.config";
    string configPath = System.IO.Path.Combine(AppContext.BaseDirectory, configFileName);

    LogManager.ThrowConfigExceptions = true;

    if (!System.IO.File.Exists(configPath))
        return LogManager.CreateNullLogger();

    LogManager.Configuration = new XmlLoggingConfiguration(configPath);

    Logger logger = LogManager.GetLogger("Startup");
    logger.Info("Debug logging enabled via config file");
    logger.Info("Loaded logging configuration from {configPath}", configPath);
    return logger;
}

static string? TryGetArg(string name, string[] argv)
{
    Logger logger = LogManager.GetLogger(nameof(TryGetArg));
    string lowerName = name.ToLowerInvariant();
    for (int i = 0; i < argv.Length; i++)
    {
        string arg = argv[i].ToLowerInvariant();
        if (arg == $"/{lowerName}" || arg == $"-{lowerName}" || arg == $"--{lowerName}")
        {
            if (argv.Length > i + 1)
            {
                logger.Debug("Resolved argument {argumentName}", name);
                return argv[i + 1];
            }

            logger.Error("Argument {argumentName} was specified without a value", name);
            throw new Exception($"Argument for '{argv[i]}' is missing");
        }
    }

    logger.Trace("Argument {argumentName} not present", name);
    return null;
}

static void HandleAzureCredentials(Uri givenUri)
{
    Logger logger = LogManager.GetLogger(nameof(HandleAzureCredentials));
    logger.Info("Handling Azure credential prompt for {uri}", givenUri);

    string path = PluginManager.Instance
        .FindAvailablePluginsAsync(CancellationToken.None).Result
        .Select(p => p.PluginFile.Path)
        .First(p => p.EndsWith("CredentialProvider.Microsoft.dll"));

    logger.Debug("Resolved Microsoft credential provider path: {pluginPath}", path);

    string singleLine = Environment.NewLine;
    string doubleLine = singleLine + singleLine;
    string uri = givenUri.ToString();
    string command = $"dotnet {path} -uri {uri}";
    string instruction = $"{doubleLine}In order to authenticate to {uri} you must first run:{doubleLine}{command}{doubleLine}";
    string divider = "    **********************************************************************";
    string message = doubleLine
        + "ATTENTION: User interaction required." + singleLine
        + divider
        + instruction
        + divider + doubleLine;

    logger.Info("Returning interactive authentication instruction for {uri}", givenUri);
    WriteCredentialResponse(message: message);
    Environment.Exit(2);
}

static int Impl(string[] argv)
{
    Logger logger = LogManager.GetLogger(nameof(Impl));
    logger.Info("Starting credential resolution");

    Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", "dotnet");
    logger.Debug("Set DOTNET_HOST_PATH to dotnet");

    string? givenUriStr = TryGetArg("uri", argv);
    if (givenUriStr == null)
    {
        logger.Error("The uri argument is required");
        throw new Exception("the -uri argument is required");
    }

    Uri givenUri = new Uri(givenUriStr);
    bool nonInteractive = HasFlag(argv, "nonInteractive");
    bool isRetry = HasFlag(argv, "isRetry");

    logger.Info("Resolving credentials for {uri}", givenUri);
    logger.Debug("nonInteractive={nonInteractive}; isRetry={isRetry}", nonInteractive, isRetry);

    var plugins = new SecurePluginCredentialProviderBuilder(
            pluginManager: PluginManager.Instance,
            canShowDialog: true,
            logger: NuGet.Common.NullLogger.Instance)
        .BuildAllAsync().Result
        .Where(p => p != null)
        .ToList();

    logger.Info("Discovered {pluginCount} credential plugin(s)", plugins.Count);
    logger.Debug("Plugin ids: {pluginIds}", string.Join(", ", plugins.Select(p => p!.Id)));

    static bool IsAzureUri(Uri uri) =>
        new[]
        {
            ".pkgs.vsts.me",           // DevFabric
            ".pkgs.codedev.ms",        // DevFabric
            ".pkgs.codeapp.ms",        // AppFabric
            ".pkgs.visualstudio.com",  // Prod
            ".pkgs.dev.azure.com"      // Prod
        }.Any(h => uri.Host.EndsWith(h));

    System.Net.ICredentials? credentials = null;
    foreach (var plugin in plugins)
    {
        bool isAzureProvider = plugin!.Id.EndsWith("CredentialProvider.Microsoft.dll");
        logger.Info("Querying plugin {pluginId}", plugin.Id);
        logger.Debug("Plugin {pluginId} isAzureProvider={isAzureProvider}", plugin.Id, isAzureProvider);

        var result = plugin.GetAsync(
            givenUri,
            proxy: null!,
            CredentialRequestType.Unauthorized,
            message: "",
            isRetry,
            isAzureProvider || nonInteractive,
            CancellationToken.None).Result;

        if (result == null)
        {
            logger.Debug("Plugin {pluginId} returned no result", plugin.Id);
            continue;
        }

        logger.Debug("Plugin {pluginId} returned credentials={hasCredentials}", plugin.Id, result.Credentials != null);

        if (isAzureProvider && result.Credentials == null && IsAzureUri(givenUri))
        {
            logger.Info("Azure plugin requires interactive flow for {uri}", givenUri);
            HandleAzureCredentials(givenUri);
            continue;
        }

        if (result.Credentials != null)
        {
            credentials = result.Credentials;
            logger.Info("Credentials acquired from plugin {pluginId}", plugin.Id);
            break;
        }
    }

    if (credentials != null)
    {
        var cred = credentials.GetCredential(givenUri, "Basic");
        if (cred != null)
        {
            logger.Info("Returning credentials for user {userName}", cred.UserName);
            WriteCredentialResponse(cred.UserName, cred.Password);
            return 0;
        }

        logger.Warn("Credential container was returned but no Basic credential was available");
    }

    logger.Warn("No credentials were resolved for {uri}", givenUri);
    return 1;
}

Logger startupLogger = ConfigureLogging();

try
{
    try
    {
        return Impl(args);
    }
    catch (Exception e)
    {
        startupLogger.Error(e, "Credential provider failed");
        Console.Error.Write($"Error: {e}");
        return 137;
    }
}
finally
{
    startupLogger.Info("Shutting down credential provider");
    LogManager.Shutdown();
    Console.Out.Flush();
    Console.Error.Flush();
}
