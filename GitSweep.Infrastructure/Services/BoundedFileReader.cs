namespace GitSweep.Infrastructure.Services;

/// <summary>
/// Resultado de <see cref="BoundedFileReader"/>: solo <see cref="BoundedReadStatus.Ok"/>
/// trae contenido.
/// </summary>
internal enum BoundedReadStatus
{
    /// <summary>El fichero se leyó entero y cabe en el límite.</summary>
    Ok,

    /// <summary>La ruta es un enlace simbólico (o punto de reanálisis); no se abrió.</summary>
    Link,

    /// <summary>Longitud 0: nunca es válido y puede ser una FIFO; no se abrió.</summary>
    Empty,

    /// <summary>Supera el límite; no se leyó más allá de límite + 1 bytes.</summary>
    TooLarge,
}

/// <summary>Estado de la lectura y, solo si es <see cref="BoundedReadStatus.Ok"/>, los bytes leídos.</summary>
internal readonly record struct BoundedReadResult(BoundedReadStatus Status, MemoryStream? Content);

/// <summary>
/// Lee ficheros no confiables (<c>.gitsweep.json</c>, entradas de la papelera),
/// que pueden venir en un repo clonado o en un archivo tar. Un único punto
/// concentra tres defensas:
/// <list type="bullet">
/// <item>Nunca sigue enlaces simbólicos.</item>
/// <item>Rechaza longitud 0 <b>antes de abrir</b>: una FIFO (que un tar puede
/// traer) mide 0 bytes, pasa un chequeo de tamaño máximo y <c>open(2)</c> sin
/// <c>O_NONBLOCK</c> se bloquea para siempre hasta que aparezca un escritor.
/// Un fichero vacío tampoco es nunca un config ni una entrada válidos.</item>
/// <item>Lee a un búfer de como máximo <c>límite + 1</c> bytes y devuelve esos
/// bytes ya en memoria: nunca lee hasta el final de un flujo sin cota, aunque
/// el tamaño declarado por el sistema de ficheros mienta o el fichero crezca
/// entre la comprobación y la lectura.</item>
/// </list>
/// Quien llama deserializa desde el <see cref="MemoryStream"/> devuelto con
/// <c>JsonSerializer.Deserialize(Stream)</c>, que sigue saltándose un BOM UTF-8
/// inicial como antes.
/// </summary>
internal static class BoundedFileReader
{
    public static async Task<BoundedReadResult> ReadAsync(string path, int maxBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        // Todas las comprobaciones van antes de abrir: abrir ya es lo que
        // bloquea en una FIFO.
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return new BoundedReadResult(BoundedReadStatus.Link, null);
        if (info.Length == 0)
            return new BoundedReadResult(BoundedReadStatus.Empty, null);
        if (info.Length > maxBytes)
            return new BoundedReadResult(BoundedReadStatus.TooLarge, null);

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        return await ReadFromStreamAsync(stream, maxBytes, ct);
    }

    /// <summary>
    /// Lee como mucho <paramref name="maxBytes"/> + 1 bytes de
    /// <paramref name="stream"/>: el byte de más basta para saber que el
    /// contenido excede el límite sin leer nada más.
    /// </summary>
    internal static async Task<BoundedReadResult> ReadFromStreamAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[maxBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }

        if (count > maxBytes)
            return new BoundedReadResult(BoundedReadStatus.TooLarge, null);
        // Vaciado entre la comprobación de longitud y la lectura.
        if (count == 0)
            return new BoundedReadResult(BoundedReadStatus.Empty, null);

        return new BoundedReadResult(BoundedReadStatus.Ok, new MemoryStream(buffer, 0, count, writable: false));
    }
}
