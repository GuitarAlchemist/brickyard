# Manuel de Delta

## Bienvenue

Delta est un petit robot à qui l'on parle à voix haute, en français ou en anglais. Il se tient dans une halle de briques et sert de voix à Claude Code : il entend votre micro, transmet ce que vous dites à votre session, et Claude répond par sa bouche.

Ce manuel est un livre posé sur le lutrin de la halle (touche 9). Les chapitres sont à gauche : un clic en ouvre un, et les flèches en bas tournent les pages.

Ce que vous dites est transcrit sur votre ordinateur. Seul le texte part vers Claude, comme ce que vous tapez.

## Parler à Delta

- Commencez vos phrases par « Delta, … », ou par « Claude, … » pour parler à son cerveau.
- La conversation continue ensuite sans le nom : chaque phrase et chaque réponse la prolongent de 30 secondes, qu'un anneau décompte en bas à gauche de la fenêtre.
- Une phrase qu'il laisse passer faute de « Delta » s'affiche cinq secondes sous le robot, avec la façon de l'appeler : vous savez ainsi qu'il l'a entendue.
- Il ignore une phrase qui dit « je ne te parle pas », ou qui appelle d'abord une autre session : Brutal, Gaïa, Indigo, Amber, Codex ou IX.
- En français, il obéit seul à quelques phrases courtes : « Delta, mets-toi en haut à gauche », « cache-toi », « Delta, montre-moi les sessions ».

Dans Claude Code, il y a deux façons de lui parler : relancer Claude Code avec le canal de Delta, ou lancer /delta:talk dans n'importe quelle session, jusqu'à « au revoir ».

## Les boutons de la fenêtre

- La langue (FR, FR·EN, EN), ou la touche L. Avec deux langues, Delta entend les deux et répond dans la langue de chaque phrase. Vous pouvez aussi le dire : « parle seulement français », « passe en mode bilingue ».
- Le micro coupe le micro de l'ordinateur, barré de rouge, puis le rallume. Un téléphone jumelé lui parle toujours.
- Le téléphone, ou la touche P, montre le code QR qui jumelle un téléphone quand la passerelle vient d'en créer un. Le code vit deux minutes.
- Inviter montre un code QR et un lien qui laissent des invités regarder la fenêtre.
- La touche M ouvre l'étagère : les sessions Claude Code, les plugins et les marketplaces.

## Se déplacer

### À pied

- ZQSD, WASD ou les flèches : avancer. Maj : sprint. Espace : sauter. C ou Ctrl : s'accroupir, et en sprint glisser.
- A et E (en QWERTY : Q et E) : se pencher. Verr. Maj : marcher lentement.
- Clic droit maintenu, ou Ctrl puis un clic : regarder autour.

### En vol

- F passe de la marche au vol et inversement. En vol, Espace ou E monte, C descend, Maj va plus vite. La molette règle l'allure.

### Les vues

- 1 : la dérive autour de Delta. Origine ou Entrée : revenir à Delta.
- 2 le bureau des agents, 3 Gaïa, 4 toute la halle, 5 les horloges, 6 la borne d'arcade, 7 l'étage des nœuds, 8 la galerie, 9 le lutrin.
- Maj + 1 à 5 : la vue filmée. 0 : la cinématique, qu'une touche ou un clic arrête.
- Tab : la roue d'outils. F1 : la carte des touches.

## Le bureau des agents

Touche 2. Chaque session Claude Code a son poste : un bureau et un petit Delta. Les postes forment une tour ronde : le panneau d'un espace wmux, puis ses robots. Un anneau plein, la tour monte d'un étage. Les anneaux tournent lentement.

- Le robot tape quand sa session travaille, allume une balise rouge quand elle attend une réponse, et saute bras levés quand elle a fini.
- Un robot arrêté baisse la tête. Ambre : sa session ne tourne plus. Gris : on ne sait rien d'elle.
- Un clic sélectionne un robot : une petite lumière tourne au-dessus de lui et son anneau s'arrête. Un double clic ouvre son volet dans wmux.

## La halle

- Gaïa (touche 3) : sa pancarte dit comment va le pont vers Gaïa.
- Les horloges d'Ottawa et de Paris (touche 5). H les cache ou les montre.
- La borne d'arcade (touche 6) : un clic dessus lance PARSEC. Toutes les touches sont alors au jeu, et Échap en sort.
- L'étage des nœuds (touche 7) : 21 nœuds marins en cordage, en haut de l'escalier.
- L'atelier d'IX : le nœud qu'IX décrit, sur son socle, et sa table d'origami.

## La galerie et le lutrin

Touche 8. Le mur de gauche porte les cours du site learn, ses démos et ses artifacts. Au fond, le fichier de l'université Streeling a un tiroir par département.

- Un clic sur un livre ou sur un tiroir l'ouvre ici, sur le lutrin. Un clic sur une démo ou un artifact ouvre sa page dans le navigateur.
- À gauche, le sommaire : un clic lit une leçon. À droite, la page, et les flèches la tournent.
- « Ouvrir sur le site » ouvre la page dans le navigateur. « Manuel de Delta », en bas à gauche, revient à ce livre.
- Les pages viennent du site spareilleux.github.io/learn seulement, en français d'abord. Elles sont gardées un jour, et se lisent hors ligne.

## Image et performances

- B : la brume. T : les légendes de la pièce.
- F6 : max out. F7 : DLSS sur l'image normale (la salle dessinée plus petite puis agrandie, les bords lissés). F9 : le path tracing. F10 : la carte RTX.

## Gestes de la main

Delta voit quelques gestes quand vous le décidez : uv run hands\delta_hands.py allume la caméra jusqu'à Ctrl+C.

- Un pouce levé le fait sourire.
- Le signe « je t'aime » envoie des cœurs.
- Un salut de la main ouvre la conversation.

Les images restent en mémoire sur votre ordinateur : seul le nom du geste part vers le cerveau de Delta.
