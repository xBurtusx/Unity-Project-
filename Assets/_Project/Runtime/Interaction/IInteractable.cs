using UnityEngine;

namespace HorrorHouse
{
    /// <summary>
    /// Anything the player can look at and use.
    ///
    /// The brief asks for one interaction framework rather than a bespoke system per object, so
    /// this interface is deliberately tiny: everything the player can touch - switches, doors,
    /// drawers, the fridge, a note on the table - implements these three members and nothing more.
    /// The interactor never knows what it is talking to.
    /// </summary>
    public interface IInteractable
    {
        /// <summary>
        /// Verb shown in the prompt, e.g. "Open", "Turn on the light", "Read".
        /// Written fresh each frame, so it can reflect current state.
        /// </summary>
        string Prompt { get; }

        /// <summary>
        /// False hides the prompt entirely. Use for things that are locked, out of reach, or not
        /// yet part of the story - a prompt the player cannot act on just teaches them to ignore
        /// prompts.
        /// </summary>
        bool CanInteract { get; }

        void Interact(GameObject interactor);
    }
}
