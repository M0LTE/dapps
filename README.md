# DAPPS

Distributed Asyncronous Packet Pub-Sub

The idea of DAPPS is to overlay an asyncronous pub-sub messaging subsystem over a packet radio network.

Ultimately, the vision is for an application at one node to be able to post a message to a queue at a remote node, without caring about the details of delivery whatsoever.

Realtime connectivitity is not a goal. Think of this roughly as packet mail for application developers.

This does not replace a packet node, but complements it (and indeed requires one).

This does not deliver any user-facing functionality on its own, but simplifies building of network applications.

This does not replace packet mail, but could piggy back on some the peering/routing arrangements in place for mail, particularly in its early days. It could also potentially enhance mail delivery.

This does not replace AX.25, but can run over it. It could also run over other transports, so conceptually isn't limited strictly to packet radio.

## Previous writing

Caution, may not align with current thinking.

https://gist.github.com/M0LTE/be1fd071ca1867703d1f2d4c17fabca2