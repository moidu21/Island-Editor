# Anno1800IslandTool v40 - performance CPU/GPU

## Objectif

La v40 vise d'abord les problèmes mesurables : sous-utilisation CPU, calculs de pinceau 3D qui rescannent trop de vertices, rebuilds terrain coûteux et absence d'une frontière claire entre calcul CPU et futur calcul GPU.

## Changements v40 inclus

- `TerrainComputeScheduler` centralise le nombre de threads CPU utilisés par les traitements terrain.
- Le nombre maximal de workers peut être forcé avec la variable d'environnement `ANNO1800_ISLAND_TOOL_MAX_THREADS`.
- Le ThreadPool est préchauffé avec davantage de workers au démarrage de l'application.
- `TerrainBrushSpatialIndex` indexe les vertices du LOD actif par cellules spatiales RD3D.
- Le pinceau 3D ne parcourt plus toutes les pages du LOD à chaque coup : il interroge uniquement les cellules proches du rayon.
- Le mode Smooth du pinceau utilise le même index spatial au lieu d'une moyenne sur toute la liste touchée.
- La resynchronisation coarse depuis le mesh utilise également l'index spatial pour retrouver les vertices proches.
- Le résumé 3D affiche maintenant le backend CPU parallèle, le niveau de rendu GPU WPF et les statistiques du dernier coup de pinceau.

## GPU

WPF/Viewport3D rend déjà via Direct3D quand `RenderCapability.Tier` indique un rendu matériel disponible. Cela accélère l'affichage, mais ce n'est pas encore un vrai compute shader pour modifier les données terrain.

Pour un vrai backend GPU de calcul terrain, la prochaine étape recommandée est d'ajouter un projet séparé, par exemple `Anno1800IslandTool.Gpu`, afin de ne pas rendre le core dépendant de DirectX. Les premiers kernels candidats sont :

1. prévisualisation heightmap -> vertices/normales,
2. génération de normalmaps/tintmaps,
3. application de grands pinceaux heightmap,
4. classification hauteur -> matériau pour les transitions côte/plage/eau.

Le backend CPU doit rester le fallback obligatoire : toutes les machines Windows n'ont pas un GPU compatible compute, et le format Anno doit rester exportable sans accélération matérielle.

## Ajouts v40.2

- Overlay CPU/GPU en deux lignes pour éviter le chevauchement en haut de la fenêtre 3D.
- Coalescing des rebuilds : une seule reconstruction fraîche est relancée après annulation d'une reconstruction encore active.
- Preview interactive plus légère : décimation temporaire et normales rapides pendant peinture/navigation, retour en qualité complète au rebuild final.


## Ajouts v40.3

- Suppression de la décimation temporaire/preview ultra-light pendant peinture et navigation.
- Le rebuild coalescé reste actif, mais la scène affichée garde la qualité complète pour permettre de redessiner précisément.
- Configuration solution ajoutée pour Release|x64 et démarrage orienté UI.
