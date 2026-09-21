namespace TproxyRelay;

/// <summary>
/// The single owner of the managed-key change ritual (ARCH-004): store
/// mutation, live-session teardown and the supervisor registry re-export
/// always happen in this order and in one place, shared by the Admin API
/// and the Telegram bot.
/// </summary>
internal static class KeyManager
{
    /// <summary>Reloads managed profiles and re-exports the supervisor registry.</summary>
    public static void RefreshAndExport(RelayOptions opt, KeyStore store, ProfileRegistry registry)
    {
        registry.ReplaceManaged(store.ListKeys(includeRevoked: false).Where(k => k.Active).Select(k =>
            new RelayProfile(k.Id, k.Name, Convert.FromHexString(k.SecretHex),
                opt.BackendHostName, k.BackendPort, opt.CarrierMode)));
        store.ExportRegistry(Convert.ToHexString(opt.Secret).ToLowerInvariant());
    }

    /// <summary>Returns closed session count, or -1 when the key is unknown/already revoked.</summary>
    public static int Revoke(RelayOptions opt, KeyStore store, ProfileRegistry registry,
        RelayHub hub, string id)
    {
        if (store.Get(id) is not { RevokedUtc: null })
            return -1;
        store.Revoke(id);
        var closed = hub.CloseAllSessionsForKey(id, "key revoked");
        RefreshAndExport(opt, store, registry);
        return closed;
    }

    /// <summary>Returns closed session count, or -1 when the key is unknown/already revoked.</summary>
    public static int Pause(RelayOptions opt, KeyStore store, ProfileRegistry registry,
        RelayHub hub, string id)
    {
        if (!store.SetPaused(id, true))
            return -1;
        var closed = hub.CloseAllSessionsForKey(id, "key paused");
        RefreshAndExport(opt, store, registry);
        return closed;
    }

    /// <summary>False when the key is unknown or not active.</summary>
    public static bool Resume(RelayOptions opt, KeyStore store, ProfileRegistry registry, string id)
    {
        if (!store.SetPaused(id, false))
            return false;
        RefreshAndExport(opt, store, registry);
        return true;
    }
}
