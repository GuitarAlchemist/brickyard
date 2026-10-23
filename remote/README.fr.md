# delta-remote

[English](README.md)

Montre la fenêtre de Delta sur un téléphone ou un autre ordinateur, en direct, par WebRTC, et permet de parler avec
Delta depuis cet appareil. Un appareil à la fois.

```powershell
cd remote; go build; .\delta-remote.exe
```

Il affiche un lien, `https://<adresse>:8810/#code=<code>`, et le confie à la fenêtre de Delta pour sa carte QR
(l'icône du téléphone). Scanner le QR appaire l'appareil en une seule étape ; il n'a ensuite plus besoin de code
pendant 90 jours. Le code est renouvelé toutes les 100 s, et dès qu'un appareil s'en sert, donc la carte contient
toujours un QR qui marche tant que la passerelle tourne ; `%LOCALAPPDATA%\delta-remote\link.txt` contient le même
lien. `-forget-devices` désappaire tous les appareils.

- **Parler :** la page montre la conversation en bulles, chaque réponse sous la phrase à laquelle elle répond. Il y a
  deux façons de parler :
  - **Maintenir** le bouton et parler ; ce que le micro a entendu part au relâchement.
  - **Appeler** lance un appel. Le micro reste ouvert, et la page découpe ce qu'elle entend en phrases d'après le
    volume, en envoyant chacune quand tu t'arrêtes de parler. Elle n'écoute pas pendant que Delta parle, donc elle ne
    l'entend jamais. L'écran reste allumé jusqu'à **Raccrocher**.

  Chaque phrase part vers le cerveau de Delta en WAV 16 kHz (30 s au plus), qui la transcrit et la prend comme dite à
  Delta. Un carillon sonne dans la pièce. L'appareil joue les réponses à ses propres phrases. Il montre les réponses
  aux phrases dites à l'ordinateur sans le son, avec un ▶ pour en écouter une ici, et ne joue Delta parlant de
  lui-même que pendant un appel. Les `/hear`, `/said` et `/link` du cerveau exigent le secret qu'il écrit à chaque
  démarrage dans `%LOCALAPPDATA%\delta\remote.secret` ; la passerelle le relit à chaque requête. La logique de la page
  qui se passe de navigateur est testée par `node --test` (`talk.test.js`).

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
