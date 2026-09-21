# Contrôle de validation sans affichage

Exécuter sous Windows avec .NET 8 depuis la racine des sources :

    dotnet run --project tests/Anno1800IslandTool.CommitChecks -- "chemin\original.zip" "chemin\nouveau-test.zip"

Ce contrôle est prévu pour moderate_l_10 (zone 6000..6800). Il modifie une copie
importée en mémoire, ajoute une rivière et vérifie conservation des hauteurs
après validations répétées, fermeture et export/réimport. Il ne montre aucune
fenêtre, ne crée pas de viewport GPU et n'automatise pas l'interface utilisateur.
Il ne teste pas les événements de souris ni le rendu en jeu.
