using System.Text;
using System.Threading.Channels;

namespace ShiftBridge.Presentation;

/// <summary>
/// Recopie du journal dans un fichier (%APPDATA%\ShiftBridge\log.txt), écrite par une tâche de fond : l'interface ne fait plus
/// d'écriture disque à chaque ligne (auparavant un File.AppendAllText par ligne, sur le thread UI, jusqu'à 60 fois par seconde
/// en journal détaillé). Le fichier tourne dès qu'il dépasse <see cref="MaxBytes"/> (et plus seulement au lancement).
/// </summary>
internal sealed class LogFileWriter
{
    public const long MaxBytes = 5_000_000;

    private readonly string _path;
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public LogFileWriter(string path)
    {
        _path = path;
        _loop = Task.Run(WriteLoopAsync);
    }

    /// <summary>Le fichier par défaut, créé à la première utilisation ; null si le dossier est inaccessible.</summary>
    public static LogFileWriter? Default => _default.Value;

    private static readonly Lazy<LogFileWriter?> _default = new(() =>
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShiftBridge");
            Directory.CreateDirectory(dir);
            return new LogFileWriter(Path.Combine(dir, "log.txt"));
        }
        catch { return null; }
    });

    /// <summary>Ne bloque jamais : la ligne est mise en file (les plus anciennes sont perdues si le disque ne suit pas).</summary>
    public void Write(string line) => _lines.Writer.TryWrite(line);

    private readonly Task _loop;

    /// <summary>À l'arrêt de l'appli : écrit les dernières lignes (au plus <paramref name="timeout"/>).</summary>
    public async Task CloseAsync(TimeSpan timeout)
    {
        _lines.Writer.TryComplete();
        try { await _loop.WaitAsync(timeout); } catch { /* tant pis pour les dernières lignes */ }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            StreamWriter? writer = null;
            try
            {
                await foreach (var batch in ReadBatchesAsync())
                {
                    if (writer is null || writer.BaseStream.Length > MaxBytes)
                    {
                        writer?.Dispose();
                        writer = Open();
                    }
                    foreach (var line in batch) writer.WriteLine(line);
                    writer.Flush();
                }
            }
            finally { writer?.Dispose(); }
        }
        catch { /* disque plein, fichier verrouillé… : le journal à l'écran reste disponible */ }
    }

    /// <summary>Regroupe les lignes arrivées ensemble : une seule écriture disque pour une rafale.</summary>
    private async IAsyncEnumerable<List<string>> ReadBatchesAsync()
    {
        var reader = _lines.Reader;
        while (await reader.WaitToReadAsync())
        {
            var batch = new List<string>();
            while (batch.Count < 500 && reader.TryRead(out var line)) batch.Add(line);
            yield return batch;
        }
    }

    private StreamWriter Open()
    {
        var fi = new FileInfo(_path);
        if (fi.Exists && fi.Length > MaxBytes) File.Move(_path, _path + ".old", overwrite: true);
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = Environment.NewLine };
    }
}
