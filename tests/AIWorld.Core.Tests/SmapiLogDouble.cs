// The linked production chat server only needs SMAPI logging. No game/UI/HTTP startup in these tests.
namespace StardewModdingAPI;

public enum LogLevel { Trace, Debug, Info, Warn, Error, Alert }
public interface IMonitor { void Log(string message, LogLevel level = LogLevel.Debug); }
