# Gaia V1.2 — prototype futur, intégration hôte requise

> État au 2026-10-29 : la mise à niveau hôte ci-dessous est faite sur `feat/gaia-v12` (voir NOTES.md,
> « Gaia V1.2's planet, loaded by the host »). V1.2 n'est ni installée ni activée en live.

Base : `fb478ae93ca9f854916759fa426df9867c6bc1bc`. Branche : `codex/gaia-v12-planet`.
Ce prototype reste local et séparé de la scène active. Aucune candidate déposée, activation,
relance, installation, requête réseau, audio ou modification du brain/renderer.

## Livrable

`module-contracts/ModuleTriangles.cs` ajoute une capacité facultative `ISceneTriangleModule : ISceneModule`
et trois types CPU : vertex position/normale/couleur, triangle, liste de triangles.
`SceneModule.cs` et tous les types/méthodes/constructeurs V1/V1.1 restent inchangés.
L'identité/version de l'assembly de contrat reste `Delta.SceneModules.Contracts, 1.0.0.0` :
ajout de types, sans remplacement de l'interface existante. Les anciennes DLL sont testées
contre ce contrat enrichi. Cela ne rend pas la nouvelle DLL compatible avec l'ancien contrat.

`modules/GaiaPlanet/GaiaPlanet.Module.csproj` produit `GaiaPresence.Module.dll`, version réelle
`1.2.0.0`, type `Delta.GaiaPresence.GaiaModule`, identifiant `gaia`, conformément au loader existant.
Le projet GaiaPresence V1/V1.1 n'est pas modifié. Ne pas référencer les deux assemblies de même
nom dans un même programme : le loader existant les isole dans ses contextes collectables.

La planète statique utilise une sphère latitude/longitude 96 × 40, sans triangles dégénérés
aux pôles, normale radiale continue, couleurs océans/continents/nuages procédurales,
et un anneau opaque fin, incliné. Aucun fichier texture, transparence ou nouveau shader.
Les nuages sont une variation de couleur de surface, pas un volume animé.
Centre `(0.88, 0.69, 0.16)`, rayon `0.25`, rayon extérieur anneau `0.294`.
La plaque de statut reste explicitement « Gaia - non raccordée » / « Gaia - disconnected ».
Version, offre et clic restent la responsabilité indépendante de l'hôte existant.

Le maillage est créé une seule fois, exposé par une collection en lecture seule et partagé entre
les langues. Dispose vide les frames et libère la référence au maillage ; appels suivants refusés,
Dispose répétable. Aucun handle GPU, thread, timer ou événement statique ne traverse le contrat.

## Budgets et validation

- Plafond : 8 192 triangles, soit 24 576 vertices après expansion ; plus les budgets V1 existants
  de 4 096 boîtes / 16 textes. Ce plafond est CPU, avant allocation GPU.
- Réel : 7 680 triangles = 7 488 sphère + 192 anneau ; 23 040 vertices.
- Avec le layout actuel Vertex de 68 octets : 1 566 720 octets de vertices additionnels, estimation
  hors plaques, buffers temporaires et overhead Vulkan. Aucun buffer GPU alloué ici.
- Validation : positions finies dans `(-16,16)`, normales unitaires tolérance 0.001 sur leur norme
  au carré, couleurs finies `[0,1]`, aire non nulle, winding cohérent avec les trois normales.
- La validation n'impose pas qu'un maillage générique soit fermé. Le test propre à la planète
  vérifie deux triangles adjacents par arête de sphère, y compris couture et pôles.

## Mise à niveau hôte minimale, à intégrer par le propriétaire unique

**La scène actuelle ne peut pas charger cette V1.2.** Son assembly de contrat chargé en mémoire
ne connaît pas `ISceneTriangleModule` ; le test avec sa vraie DLL constate un TypeLoadException.
Remplacer uniquement une DLL sur disque ne met pas à niveau l'assembly déjà chargé.
Le propriétaire doit organiser le bootstrap de l'hôte enrichi ; ce prototype ne le fait pas.

1. Recompiler l'hôte avec le contrat enrichi et partager cette unique assembly de contrat dans
   `LoadedSceneModule.CodeContext` comme aujourd'hui. Aucune référence statique au projet GaiaPlanet.
2. Dans LoadedSceneModule, détecter génériquement `module is ISceneTriangleModule`, appeler
   RenderTriangles et transporter ce résultat optionnel avec ModuleVisual jusqu'à createSurface.
   Les anciens modules donnent une valeur nulle. Ne pas ignorer silencieusement la capacité.
   Pour garder les tests V1/V1.1 intacts, conserver la signature de factory existante via un overload ;
   ce chemin ancien doit refuser une frame avec triangles au lieu de les perdre.
3. Avant tout upload : contrôler Count, copier la liste dans un snapshot CPU borné, valider ce snapshot
   avec ModuleTriangleValidation.Validate, puis compiler les vertices. Ne pas valider une liste puis
   relire une autre version mutable. Utiliser Matte, positions/couleurs/normales fournies.
4. Adapter le vertex existant sans shader supplémentaire : tangent constant non parallèle aux normales
   de chaque triangle (par exemple un axe du plan du triangle), et Edge `(0,0,1,1)` pour ses trois vertices.
   Dans LowFragmentShader actuel, cela garde toEdge loin du bord et lean nul ; ne pas mettre tangent à zéro
   (normalize indéfini), ni Edge entièrement nul (smoothstep avec largeur nulle). Flat utilise déjà la normale
   interpolée. Cette conversion reste à compiler et vérifier sur Vulkan par le propriétaire.
5. Appendre ces vertices au même mesh/surface hôte que les boîtes/textes. Conserver l'ordre existant :
   préparer nouvelles ressources → fence GPU → publier la surface → Dispose ancienne surface/module.
   Toute erreur de contrat, validation, allocation ou fence garde la version précédente.
6. Préserver plaques/clics/langue/connexion, copie V1.1 retenue, index et hash vérifiés, retour explicite.
   Ajouter alors un test isolé V1.1 → V1.2 → V1.1 avec le port ressources et, seulement ensuite,
   une preuve Vulkan bornée. Rien ne doit être installé automatiquement dans les répertoires live.

Aucune animation n'est proposée ici. La clé actuelle de SceneModuleHost met le mesh en cache par
langue/offre/erreur ; animer Update seul ne mettrait pas les buffers à jour. Un futur mouvement devrait
utiliser une transformation hôte plutôt qu'un rebuild du maillage par frame.

## Vérification locale reproductible, sans réseau ni scène

Depuis ce worktree, avec le SDK .NET 10 déjà installé :

```powershell
dotnet restore module-mesh-tests/SceneModules.MeshSelfTest.csproj --source . -p:NuGetAudit=false -m:1
dotnet build module-mesh-tests/SceneModules.MeshSelfTest.csproj --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet module-mesh-tests/bin/Debug/net10.0/SceneModules.MeshSelfTest.dll artifacts/planet-cpu.png artifacts/legacy/contracts-v1.dll

dotnet restore module-tests/SceneModules.SelfTest.csproj --source . -p:NuGetAudit=false -m:1
dotnet build module-tests/SceneModules.SelfTest.csproj --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet module-tests/bin/Debug/net10.0/SceneModules.SelfTest.dll artifacts/legacy/v1.dll artifacts/legacy/v2.dll
```

Le second argument du nouveau selftest est optionnel et désigne la copie de l'ancien contrat.
Les copies legacy sont des entrées de preuve locales, pas des candidates. Les fichiers sources actifs
ont été lus et copiés sans modification ; hashes V1/V1.1 :

- V1 : `B4124285316A5A95016AD05AAFF2ABFD6E9EE09B5974CF3DB38CDF6D151F013C`
- V1.1 : `57800313D983C522062DA97D04F65C763AEDC249D173F60AEBF34D4BB2963566`

L'aperçu `artifacts/planet-cpu.png` est un raster CPU 640 × 640 des triangles réels avec z-buffer,
couleurs interpolées et éclairage Lambert. Il a été inspecté visuellement. Il ne représente pas le shader
Vulkan, la scène complète, la lisibilité finale des plaques, les clics ou le compteur de buffers natifs.
L'encodeur PNG existant `scene/Png.cs` est réutilisé par lien source, sans modification.

Preuves : `artifacts/mesh-build.log`, `artifacts/mesh-tests.log`, `artifacts/legacy-build.log`,
`artifacts/legacy-tests.log`. Les tests V1/V1.1 existants exercent des ressources simulées, y compris
fence, échecs et aller-retour ; aucune preuve de rollback V1.2 sur GPU n'est revendiquée.
Les nouveaux tests caractérisent la géométrie et le lifecycle ; aucun résultat red-before-code valide
n'est revendiqué (la tentative initiale échouait sur les assets de restauration absents).
