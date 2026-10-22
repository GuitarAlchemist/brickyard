# delta-remote

[English](README.md)

Montre la fenêtre de Delta sur un téléphone ou un autre ordinateur, en direct, par WebRTC. Un appareil à la fois.

```powershell
cd remote; go build; .\delta-remote.exe
```

Il affiche un lien, `https://<adresse>:8810/#code=<code>`. Ouvre-le sur l'appareil dans les deux minutes : l'appareil
est alors appairé et n'a plus besoin de code. Chaque démarrage affiche un nouveau code ; `-forget-devices` désappaire
tous les appareils.

- **Ce qu'il montre :** l'unique fenêtre visible intitulée `Brickyard` de `scene.exe`, par le `gfxcapture` de ffmpeg 8
  (la capture graphique de Windows, donc la fenêtre se voit même recouverte). Aucune fenêtre de ce genre, ou plusieurs :
  rien n'est montré. ffmpeg ne tourne que pendant qu'un appareil est connecté.
- **Où il écoute :** une seule adresse (par défaut, l'adresse de ce PC sur le réseau local), en TCP 8810 et UDP 8811.
  Windows ne laisse entrer un appareil que sur un réseau classé Privé, ou avec une règle pour ces deux ports.
- **Son certificat** est le sien par défaut : le navigateur avertit une fois, et pendant que tu acceptes cet
  avertissement, un appareil du même réseau pourrait se faire passer pour la passerelle. Pour l'exclure, donne un
  certificat auquel l'appareil fait confiance, par exemple celui que `tailscale cert` crée pour le nom de cette machine
  sur le tailnet : `-ip <adresse tailnet> -cert <crt> -key <clé> -name <nom>`.
- **Ce qu'il refuse :** une requête qui nomme un autre hôte (rebinding DNS), un POST d'une autre origine, une offre d'un
  appareil non appairé, un second appareil. La page n'exécute que son propre script.

Il faut ffmpeg 8 ou plus récent, avec `gfxcapture` et `libx264`, dans le PATH (ou `-ffmpeg <chemin>`). La voix vers
Delta et depuis Delta vient ensuite, par les `/hear` et `/said` du cerveau.
