using System.Diagnostics.CodeAnalysis;

namespace SmartX.Core.Commands;

/// <summary>
/// Undo / redo history built on two <see cref="Stack{T}"/>s (the classic Command-pattern design).
/// <list type="bullet">
/// <item><see cref="Record"/> pushes a new command onto the undo stack and clears the redo stack,
/// because a new action invalidates anything that was undone before it.</item>
/// <item><see cref="TryUndo"/> pops the most recent command (Last-In-First-Out) and moves it to the redo stack.</item>
/// <item><see cref="TryRedo"/> moves it back.</item>
/// </list>
/// The undo stack is capped so memory stays bounded on a gateway device: when it overflows, the
/// oldest entries (at the bottom of the stack) are dropped.
/// </summary>
public sealed class UndoRedoHistory<T>
{
    private readonly object _gate = new();
    private Stack<T> _undo = new();
    private readonly Stack<T> _redo = new();

    public UndoRedoHistory(int capacity = 50)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int UndoCount
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count;
            }
        }
    }

    public int RedoCount
    {
        get
        {
            lock (_gate)
            {
                return _redo.Count;
            }
        }
    }

    public void Record(T command)
    {
        lock (_gate)
        {
            _undo.Push(command);
            _redo.Clear();

            if (_undo.Count > Capacity)
            {
                // ToArray() returns the stack top-first; keep the newest Capacity items.
                T[] newestFirst = _undo.ToArray();
                var kept = new T[Capacity];
                Array.Copy(newestFirst, kept, Capacity);
                Array.Reverse(kept); // the Stack constructor pushes in order, so the newest must come last
                _undo = new Stack<T>(kept);
            }
        }
    }

    public bool TryPeekUndo([MaybeNullWhen(false)] out T command)
    {
        lock (_gate)
        {
            return _undo.TryPeek(out command);
        }
    }

    public bool TryUndo([MaybeNullWhen(false)] out T command)
    {
        lock (_gate)
        {
            if (!_undo.TryPop(out command))
            {
                return false;
            }

            _redo.Push(command);
            return true;
        }
    }

    public bool TryRedo([MaybeNullWhen(false)] out T command)
    {
        lock (_gate)
        {
            if (!_redo.TryPop(out command))
            {
                return false;
            }

            _undo.Push(command);
            return true;
        }
    }

    /// <summary>Undo stack contents, most recent first (the order <c>Undo</c> would pop them).</summary>
    public T[] UndoSnapshot()
    {
        lock (_gate)
        {
            return _undo.ToArray();
        }
    }

    /// <summary>Redo stack contents, next-to-redo first.</summary>
    public T[] RedoSnapshot()
    {
        lock (_gate)
        {
            return _redo.ToArray();
        }
    }
}
