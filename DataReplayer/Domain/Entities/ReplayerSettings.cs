namespace DataReplayer.Domain.Entities;

public class ReplayerSettings
{
    public int Id { get; set; } = 1;
    public List<string> TrackersWhiteList { get; set; } = new();
    public List<string> SubscribedTopics { get; set; } = new();
    public int RetentionHours { get; set; } = 24;
    public bool IsRecordingEnabled { get; set; } = false;

    // Which segment of the topic path contains the Tracker/Device ID.
    // Example: "BADGE/9F31510F9918CE60/up/pressure" → index 1
    public int TrackerIdTopicSegmentIndex { get; set; } = 1;

    // MQTT broker connection
    public string MqttBrokerHost { get; set; } = "localhost";
    public int MqttBrokerPort { get; set; } = 1883;
    public string? MqttUsername { get; set; }
    public string? MqttPassword { get; set; }
}
