namespace NexaGrid.Infrastructure;

public sealed class StoreOptions
{
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NexaGrid",
        "controller.db");
}
