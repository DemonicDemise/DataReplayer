using System;

namespace DMMS.BuildingBlocks.Globalization
{
    public class Translation
    {
        public string? Ru { get; set; }
        public string? En { get; set; }
        public string? Kk { get; set; }

        public static Translation NewRepeated(string text) => new() { Ru = text, En = text, Kk = text };
    }
}

namespace DMMS.BuildingBlocks.Geometries
{
    public class SimplePoint
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }
}

namespace DMMS.ResourceManagement.Contracts.Enums
{
    public enum DeviceType
    {
        Tracker = 1,
        BaseStation = 2,
        Beacon = 3,
        Manual = 6
    }
}

namespace DMMS.ResourceManagement.Contracts.Constants
{
    public static class IndicatorTypes
    {
        public const string LastActivityTime = "LastActivityTime";
    }
}

namespace DMMS.Positioning.Contracts.Constants
{
    public static class RegistrationOrigins
    {
        public const string Rtls = "rtls";
        public const string Gps = "gps";
    }
}

namespace DMMS.InfrastructureMonitor.Contracts.Models
{
    using DMMS.BuildingBlocks.Globalization;

    public class IndicatorModel
    {
        public Translation? Name { get; set; }
        public string Code { get; set; } = string.Empty;
        public string? StringValue { get; set; }
        public double? NumberValue { get; set; }
    }
}

namespace DMMS.InfrastructureMonitor.Contracts.IntegrationsEvents.V1
{
    using DMMS.InfrastructureMonitor.Contracts.Models;
    using DMMS.ResourceManagement.Contracts.Enums;

    public class DeviceIndicatorValuePacketEvent
    {
        public DateTime Timestamp { get; set; }
        public string NativeId { get; set; } = string.Empty;
        public DeviceType DeviceType { get; set; }
        public IndicatorModel[] Indicators { get; set; } = Array.Empty<IndicatorModel>();
    }
}

namespace DMMS.Positioning.Contracts.TrackerRegistrations.Origin.Abstractions
{
    using DMMS.BuildingBlocks.Geometries;

    public interface IRegistrationOrigin
    {
        string TrackerId { get; }
        DateTime Timestamp { get; }
        string Origin { get; }
        Guid RegistrationId { get; }
        string? ReaderId { get; }
    }

    public interface IRtlsPreciseRegistrationOrigin : IRegistrationOrigin
    {
        SimplePoint Position { get; }
        double? CalibratedPressure { get; }
    }
}

namespace DMMS.Positioning.Contracts.IntegrationEvents.V1.TrackerRegistrations.Origin
{
    using DMMS.BuildingBlocks.Geometries;
    using DMMS.Positioning.Contracts.TrackerRegistrations.Origin.Abstractions;

    public class RtlsPrecisePositionReadEvent : IRtlsPreciseRegistrationOrigin
    {
        public Guid RegistrationId { get; set; }
        public string ReaderId { get; set; } = string.Empty;
        public string TrackerId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public SimplePoint Position { get; set; } = new();
        public string Origin { get; set; } = string.Empty;
        public float? CalibratedPressure { get; set; }
        public double? CalibratedPressureDouble => CalibratedPressure; 

        // Explicit implementations if types mismatch slightly
        double? IRtlsPreciseRegistrationOrigin.CalibratedPressure => CalibratedPressure;
    }
}
