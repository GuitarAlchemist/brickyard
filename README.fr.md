# Delta

[English](README.md)

Delta est un petit robot à qui l'on parle à voix haute, en anglais ou en français. Il se tient dans une halle de
briques dessinée avec Vulkan, écoute votre micro et parle avec une voix locale. Il sert de voix à Claude Code : Delta
vous entend et transmet ce que vous dites à votre session Claude Code. Claude fait le travail et répond par la bouche de
Delta.

![Delta qui parle, dans sa halle de briques](docs/delta.png)

## Avec Claude Code

Il vous faut Windows 10 ou 11 en 64 bits, une carte graphique dont le pilote prend en charge Vulkan 1.3, un micro et
[Claude Code](https://code.claude.com/docs/fr/overview). Un casque évite que la voix de Delta revienne dans le micro.

1. Installez le plugin. Dans Claude Code, lancez :

   ```
   /plugin marketplace add GuitarAlchemist/brickyard
   /plugin install delta@brickyard
   ```

2. Lancez `/delta:setup`. Claude vous demande dans quelle langue Delta doit entendre et parler : anglais (par défaut)
   ou français. Choisissez le français. Claude regarde ensuite ce qui est déjà installé, vous dit ce qu'il
   téléchargerait et vous demande votre accord.
   - La première fois, cela fait environ 700 Mo : Delta, Piper et une voix française, whisper.cpp et un modèle Whisper.
   - Si vous avez déjà Piper, whisper.cpp ou le modèle, donnez leurs chemins à Claude.
   - Chaque téléchargement est vérifié par son empreinte SHA-256.
   - Pour changer de langue plus tard, relancez `/delta:setup` et demandez l'autre langue.
3. Parlez à Delta, de l'une de ces deux façons :
   - **Votre voix arrive toute seule.** Relancez Claude Code avec le
     [canal](https://code.claude.com/docs/fr/channels) de Delta, puis commencez vos phrases par « Delta » :

     ```
     claude --dangerously-load-development-channels plugin:delta@brickyard
     ```

     Les canaux sont un aperçu de recherche : Claude Code vous demande de confirmer. Sur les offres Team et Enterprise,
     un administrateur doit d'abord autoriser les canaux.
   - **Dans n'importe quelle session, sans option.** Lancez `/delta:talk`. Claude écoute et répond à tour de rôle
     jusqu'à ce que vous disiez « au revoir ».

La fenêtre de Delta s'ouvre quand Claude Code démarre le serveur MCP du plugin. Si elle n'apparaît pas, lancez `/mcp`,
choisissez **delta**, puis **Reconnect**, ou redémarrez Claude Code.

Ce que Delta transmet par le canal :
- les phrases qui commencent par l'appeler, « Delta, … », ou par appeler son cerveau, « Claude, … » ;
- la suite, tant que la conversation continue : chaque phrase pour Delta et chacune de ses réponses la prolongent de
  30 secondes, qu'un anneau en bas à gauche de sa fenêtre décompte.

Une phrase qu'il laisse passer faute de « Delta » s'affiche cinq secondes sous le robot, avec la façon de l'appeler :
vous savez ainsi qu'il l'a entendue.

Un clic sur l'icône de téléphone, en haut à droite de la fenêtre, ou la touche P, montre le code QR qui jumelle un
téléphone, quand une passerelle comme celle d'IX vient d'en créer un : il vit deux minutes.

Il ignore une phrase qui dit « je ne te parle pas », ou qui appelle d'abord une autre session de l'auteur : Brutal,
Gaïa, Indigo, Amber, Codex ou IX. La conversation s'arrête alors.

La première fois que Claude utilise les outils `say` et `listen` de Delta, Claude Code vous demande votre permission.

Ce que vous dites est transcrit sur votre ordinateur. Seul le texte part vers Claude, comme ce que vous tapez.

En français, Delta obéit aussi seul à quelques phrases courtes : il déplace ou cache sa fenêtre (« Delta, mets-toi en
haut à gauche », « cache-toi ») et ouvre son étagère (« Delta, montre-moi les sessions »). Le [README anglais](README.md)
les liste toutes, et décrit le mode autonome, sans Claude Code, et le fonctionnement interne.

Delta voit aussi quelques gestes de la main, quand vous le décidez : `uv run hands\delta_hands.py` allume la caméra
jusqu'à Ctrl+C. Un pouce levé le fait sourire, le signe « je t'aime » envoie des cœurs, et un salut de la main ouvre la
conversation. Les images restent en mémoire sur votre ordinateur : seul le nom du geste part vers le cerveau de Delta,
sur le même ordinateur.

## Licences

Le code de Delta est sous [licence MIT](LICENSE). La scène est construite avec BRUTAL, d'Ahwoo Limited, sous la
licence BRUTAL v1.0 ([LICENSES/BRUTAL/LICENSE.txt](LICENSES/BRUTAL/LICENSE.txt)). [THIRD_PARTY.md](THIRD_PARTY.md)
liste les autres composants et leurs licences.
