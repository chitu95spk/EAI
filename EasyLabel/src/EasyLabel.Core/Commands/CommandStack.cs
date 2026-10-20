using System;
using System.Collections.Generic;

namespace EasyLabel.Core.Commands
{
    public sealed class CommandStack
    {
        private readonly List<ICommand> _commands = new List<ICommand>();
        private int _position;
        private int _savedPosition;

        public bool CanUndo { get { return _position > 0; } }
        public bool CanRedo { get { return _position < _commands.Count; } }
        public bool IsDirty { get { return _position != _savedPosition; } }

        public void Execute(ICommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            command.Execute();
            if (CanRedo)
            {
                if (_savedPosition > _position) _savedPosition = -1;
                _commands.RemoveRange(_position, _commands.Count - _position);
            }
            _commands.Add(command);
            _position++;
        }

        public bool Undo()
        {
            if (!CanUndo) return false;
            _commands[_position - 1].Undo();
            _position--;
            return true;
        }

        public bool Redo()
        {
            if (!CanRedo) return false;
            _commands[_position].Execute();
            _position++;
            return true;
        }

        public void MarkSaved() { _savedPosition = _position; }
        public void MarkDirty() { _savedPosition = -1; }
    }
}
