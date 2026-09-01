namespace KevinZonda.Terminal.WebBridgeProtocol;

public static class BridgeMessageTypes
{
    public const string AgentUsageChanged = "agentUsage.changed";
    public const string AgentUsageRefresh = "agentUsage.refresh";
    public const string AgentUsageRefreshResult = "agentUsage.refreshResult";
    public const string AppInitialState = "app.initialState";
    public const string AppReady = "app.ready";
    public const string AppRuntimeFailed = "app.runtimeFailed";
    public const string AppSettingsChanged = "app.settingsChanged";
    public const string ClipboardRead = "clipboard.read";
    public const string ClipboardValue = "clipboard.value";
    public const string ClipboardWrite = "clipboard.write";
    public const string RuntimeAttach = "runtime.attach";
    public const string RuntimeAttached = "runtime.attached";
    public const string RuntimeReplaced = "runtime.replaced";
    public const string SessionBinaryInput = "session.binaryInput";
    public const string SessionCheckpointAck = "session.checkpointAck";
    public const string SessionClose = "session.close";
    public const string SessionClosed = "session.closed";
    public const string SessionCreate = "session.create";
    public const string SessionCreated = "session.created";
    public const string SessionError = "session.error";
    public const string SessionExited = "session.exited";
    public const string SessionInput = "session.input";
    public const string SessionInputAck = "session.inputAck";
    public const string SessionInputNack = "session.inputNack";
    public const string SessionOutput = "session.output";
    public const string SessionOutputAck = "session.outputAck";
    public const string SessionResize = "session.resize";
    public const string SettingsFontSize = "settings.fontSize";
    public const string SettingsSaved = "settings.saved";
    public const string SystemMetricsChanged = "systemMetrics.changed";
    public const string WindowNewInstance = "window.newInstance";
    public const string WindowOpenExternal = "window.openExternal";
    public const string WindowSettings = "window.settings";
    public const string WorkspaceCommand = "workspace.command";
}
