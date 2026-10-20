using System.Collections.Generic;
using System.Windows.Forms;

namespace EasyLabel.App.Shell
{
    public enum EditorAction
    {
        Undo, Redo, Delete, Copy, Paste, Save, PreviousImage, NextImage,
        SelectTool, EditTool, RectangleTool, RotationTool, PolygonTool, EditLabel, Duplicate, AddVertex, RemoveVertex, Fit, Cancel
    }

    public sealed class HotkeyMap
    {
        private readonly Dictionary<Keys, EditorAction> _bindings = new Dictionary<Keys, EditorAction>
        {
            { Keys.Control | Keys.Z, EditorAction.Undo },
            { Keys.Control | Keys.Y, EditorAction.Redo },
            { Keys.Delete, EditorAction.Delete },
            { Keys.Control | Keys.C, EditorAction.Copy },
            { Keys.Control | Keys.V, EditorAction.Paste },
            { Keys.Control | Keys.S, EditorAction.Save },
            { Keys.PageUp, EditorAction.PreviousImage },
            { Keys.PageDown, EditorAction.NextImage },
            { Keys.V, EditorAction.SelectTool },
            { Keys.E, EditorAction.EditTool },
            { Keys.R, EditorAction.RectangleTool },
            { Keys.O, EditorAction.RotationTool },
            { Keys.P, EditorAction.PolygonTool },
            { Keys.Control | Keys.D, EditorAction.Duplicate },
            { Keys.F, EditorAction.Fit },
            { Keys.Escape, EditorAction.Cancel }
        };

        public bool TryGetAction(Keys keys, out EditorAction action)
        {
            return _bindings.TryGetValue(keys, out action);
        }

        public Keys GetKeys(EditorAction action)
        {
            foreach (KeyValuePair<Keys, EditorAction> binding in _bindings)
                if (binding.Value == action) return binding.Key;
            return Keys.None;
        }

        public int GetClassIndex(Keys keys)
        {
            if (keys >= Keys.D0 && keys <= Keys.D9) return keys - Keys.D0;
            if (keys >= Keys.NumPad0 && keys <= Keys.NumPad9) return keys - Keys.NumPad0;
            return -1;
        }
    }
}
