namespace Unbounded.Panorama.App.Services;

/// <summary>Snapshot-based undo/redo over serialized layouts.</summary>
public sealed class UndoHistory
{
    private const int Limit = 150;
    private readonly List<string> _states = [];
    private int _index = -1;

    public bool CanUndo => _index > 0;
    public bool CanRedo => _index < _states.Count - 1;

    /// <summary>Records a committed state; identical consecutive states are ignored.</summary>
    public void Record(string state)
    {
        if (_index >= 0 && _states[_index] == state)
            return;
        if (_index < _states.Count - 1)
            _states.RemoveRange(_index + 1, _states.Count - _index - 1);
        _states.Add(state);
        if (_states.Count > Limit)
            _states.RemoveAt(0);
        _index = _states.Count - 1;
    }

    public string? Undo() => CanUndo ? _states[--_index] : null;

    public string? Redo() => CanRedo ? _states[++_index] : null;
}
