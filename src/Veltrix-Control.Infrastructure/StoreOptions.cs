namespace VeltrixControl.Infrastructure;

public sealed class StoreOptions
{
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Veltrix-Control",
        "controller.db");
}
