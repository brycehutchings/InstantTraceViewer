using System.Collections.Generic;
using System.Linq;

namespace InstantTraceViewerUI
{
    // ImGui uses IDs to uniquely identify windows and widget, so we must generate unique IDs for each active window and widget.
    // HOWEVER, these unique IDs are also used for persisted settings. So these IDs must be unique across multiple active windows,
    // but we also want to "collide" them across runs when possible to maintain any saved settings across runs.
    // The current solution is to prefer a smallest number that isn't currently in use. The more concurrent windows that are open,
    // the more likely the user will get a "default" layout because there will be no persisted settings available.
    public class MinUniqueId
    {
        private static int _nextId = 1;
        private static readonly SortedSet<int> _returnedIds = new();

        public int TakeId()
        {
            int smallestReturnedId = _returnedIds.FirstOrDefault(-1);
            if (smallestReturnedId != -1)
            {
                _returnedIds.Remove(smallestReturnedId);
                return smallestReturnedId;
            }

            return _nextId++;
        }

        public void ReturnId(int id)
        {
            _returnedIds.Add(id);
        }
    }
}
