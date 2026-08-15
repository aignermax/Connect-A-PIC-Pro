namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Identifies a guessed (heuristic) pin the user chose to remove from the
/// import. Two values are enough: the GDS cell name and the final pin name
/// after normalization (e.g. <c>heur_1</c>).
/// </summary>
public sealed record GdsGuessedPin(string CellName, string PinName);
