Contrôle sans affichage de l'agrandissement de moderate_l_10 à 1920 :

dotnet run --project tests/Anno1800IslandTool.ResizeChecks -- "chemin\ile_originale_interpretee.zip"

Ce test importe une copie en mémoire, applique les métadonnées et crée la cellule
(239,239) de l'emprise. Il n'affiche aucune fenêtre, n'utilise pas Computer Use,
et ne remplace pas un essai du rendu ou de la conversion en jeu. Il peut demander
plusieurs minutes et une quantité importante de mémoire sur la grande carte.
