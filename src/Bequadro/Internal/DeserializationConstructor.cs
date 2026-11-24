using System.ComponentModel;

namespace Bequadro.Internal;

/// <summary>
/// A marker that selects the constructor generated tables use when reading. Not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct DeserializationConstructor;
