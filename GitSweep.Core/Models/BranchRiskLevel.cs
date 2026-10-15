namespace GitSweep.Core.Models;

/// <summary>Semáforo de riesgo al borrar una rama local.</summary>
public enum BranchRiskLevel
{
    /// <summary>Segura: merged real en el destino y sin commits únicos que perder.</summary>
    Safe = 0,

    /// <summary>Revisar: sin evidencia suficiente para borrar sin mirar, o copia remota sin merge.</summary>
    Review = 1,

    /// <summary>Peligrosa: trabajo único no mergeado y sin subir (total o parcialmente).</summary>
    Dangerous = 2
}
