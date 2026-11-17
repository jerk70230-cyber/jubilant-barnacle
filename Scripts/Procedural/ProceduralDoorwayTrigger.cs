using UnityEngine;

/// <summary>Detects the moment the player has actually entered the newly built room.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class ProceduralDoorwayTrigger : MonoBehaviour
{
    ProceduralRoomManager _manager;
    OneWayProceduralDoor _sourceDoor;
    ProceduralRoomInstance _destinationRoom;
    bool _consumed;

    public void Initialize(ProceduralRoomManager manager, OneWayProceduralDoor sourceDoor, ProceduralRoomInstance destinationRoom)
    {
        _manager = manager;
        _sourceDoor = sourceDoor;
        _destinationRoom = destinationRoom;
        BoxCollider box = GetComponent<BoxCollider>();
        box.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        TryNotifyManager(other);
    }

    void OnTriggerStay(Collider other)
    {
        // The trigger volume begins slightly before the player's transform has
        // crossed the doorway plane. Do not consume the signal on that early
        // entry; keep checking until the manager confirms the actual crossing.
        TryNotifyManager(other);
    }

    void TryNotifyManager(Collider other)
    {
        if (_consumed || _manager == null) return;
        if (other.GetComponentInParent<ProceduralFirstPersonController>() == null) return;
        _consumed = _manager.TryNotifyPlayerEnteredRoom(_sourceDoor, _destinationRoom);
    }
}
