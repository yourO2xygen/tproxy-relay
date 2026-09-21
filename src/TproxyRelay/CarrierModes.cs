namespace TproxyRelay;

/// <summary>Carrier transport modes (ARCH-005: no bare magic strings in
/// comparisons). The wire representation stays the config string.</summary>
public enum CarrierMode
{
    Https,
    Websocket,
    HttpsLanes,
    WebsocketLanes,
}

public static class CarrierModes
{
    public static bool TryParse(string? s, out CarrierMode mode)
    {
        switch (s)
        {
            case "https": mode = CarrierMode.Https; return true;
            case "websocket": mode = CarrierMode.Websocket; return true;
            case "https-lanes": mode = CarrierMode.HttpsLanes; return true;
            case "websocket-lanes": mode = CarrierMode.WebsocketLanes; return true;
            default: mode = CarrierMode.Https; return false;
        }
    }

    /// <summary>Config/header representation of a mode.</summary>
    public static string Wire(this CarrierMode mode) => mode switch
    {
        CarrierMode.Https => "https",
        CarrierMode.Websocket => "websocket",
        CarrierMode.HttpsLanes => "https-lanes",
        CarrierMode.WebsocketLanes => "websocket-lanes",
        _ => "https",
    };

    public static bool IsLanes(string wire) =>
        wire is "https-lanes" or "websocket-lanes";

    public static bool IsWebSocketLanes(string wire) => wire == "websocket-lanes";
}
