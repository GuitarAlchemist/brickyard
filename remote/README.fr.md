# delta-remote

[English](README.md)

Montre la fenêtre de Delta sur un téléphone ou un autre ordinateur, en direct, par WebRTC, et permet de parler avec
Delta depuis cet appareil. Un appareil à la fois.

```powershell
cd remote; go build; .\delta-remote.exe
```

Il affiche un lien, `https://<adresse>:8810/#code=<code>`, et la fenêtre de Delta le montre en QR code. Ouvre-le sur
l'appareil dans les deux minutes : l'appareil est alors appairé et n'a plus besoin de code, et le QR disparaît. Chaque
démarrage affiche un nouveau code ; `-forget-devices` désappaire tous les appareils.

- **Parler :** maintiens le bouton et parle ; au relâchement, ce que le micro a entendu part vers le cerveau de Delta
  en WAV 16 kHz (30 s au plus), qui le transcrit et le prend comme dit à Delta. Un carillon sonne dans la pièce. Les
  réponses de Delta s'affichent et se jouent sur l'appareil au moment où Delta les dit à l'ordinateur. Les `/hear`,
  `/said` et `/link` du cerveau exigent le secret qu'il écrit à chaque démarrage dans
  `%LOCALAPPDATA%\delta\remote.secret` ; la passerelle le relit à chaque requête.

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

Il faut ffmpeg 8 ou plus récent, avec `gfxcapture` et `libx264`, dans le PATH (ou `-ffmpeg <chemin>`), et un cerveau
qui a `/hear`, `/said` et `/link` pour la voix et le QR.
