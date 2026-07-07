using PathTwin.App.Logging;
using PathTwin.App.Models;

namespace PathTwin.App.Backends;

public sealed class SyncBackendFactory
{
    private readonly LogService _logService;

    public SyncBackendFactory(LogService logService)
    {
        _logService = logService;
    }

    public ISyncBackend Create(ProfileConfig profile)
    {
        var rclone = new RcloneBackend(profile.RclonePath, _logService);
        return rclone.IsAvailable ? rclone : new NativeSyncBackend(_logService);
    }
}
