using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Anno1800IslandTool.UI;

public static partial class UiLocalizationManager
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Anno1800IslandTool");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "ui-language.txt");

    private static readonly DependencyProperty OriginalContentProperty = DependencyProperty.RegisterAttached(
        "OriginalContentForLocalization", typeof(string), typeof(UiLocalizationManager), new PropertyMetadata(null));

    private static readonly DependencyProperty OriginalHeaderProperty = DependencyProperty.RegisterAttached(
        "OriginalHeaderForLocalization", typeof(string), typeof(UiLocalizationManager), new PropertyMetadata(null));

    private static readonly DependencyProperty OriginalTextProperty = DependencyProperty.RegisterAttached(
        "OriginalTextForLocalization", typeof(string), typeof(UiLocalizationManager), new PropertyMetadata(null));

    private static readonly DependencyProperty OriginalTitleProperty = DependencyProperty.RegisterAttached(
        "OriginalTitleForLocalization", typeof(string), typeof(UiLocalizationManager), new PropertyMetadata(null));

    private static readonly DependencyProperty OriginalToolTipProperty = DependencyProperty.RegisterAttached(
        "OriginalToolTipForLocalization", typeof(string), typeof(UiLocalizationManager), new PropertyMetadata(null));

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> German = new(StringComparer.Ordinal);

    static UiLocalizationManager()
    {
        BuildDictionaries();
        StartupPreference = LoadPreference();
        CurrentLanguage = Resolve(StartupPreference);
        ApplyThreadCulture();
    }

    public static UiLanguage StartupPreference { get; private set; }

    public static UiLanguage CurrentLanguage { get; private set; }

    public static event EventHandler? LanguageChanged;

    public static IReadOnlyList<(UiLanguage Language, string DisplayName)> Choices { get; } = new[]
    {
        (UiLanguage.Auto, "Auto OS"),
        (UiLanguage.French, "Français"),
        (UiLanguage.English, "English"),
        (UiLanguage.German, "Deutsch")
    };

    public static UiLanguage DetectOperatingSystemLanguage()
    {
        var code = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        return code switch
        {
            "fr" => UiLanguage.French,
            "de" => UiLanguage.German,
            _ => UiLanguage.English
        };
    }

    public static void SetStartupPreference(UiLanguage language, bool save = true)
    {
        StartupPreference = language;
        CurrentLanguage = Resolve(language);
        ApplyThreadCulture();

        if (save)
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(SettingsPath, language.ToString());
            }
            catch
            {
                // Settings are a convenience only; the UI must still start if saving fails.
            }
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string source)
    {
        if (string.IsNullOrEmpty(source) || CurrentLanguage == UiLanguage.French)
        {
            return source;
        }

        var dictionary = CurrentLanguage == UiLanguage.German ? German : English;
        return dictionary.TryGetValue(source, out var translated) ? translated : source;
    }

    public static void ApplyToOpenWindows()
    {
        foreach (Window window in Application.Current.Windows)
        {
            ApplyTo(window);
        }
    }

    public static void ApplyTo(DependencyObject root)
    {
        ApplyToObject(root);
        TraverseLogicalChildren(root);
        TraverseVisualChildren(root);
    }

    public static void FillLanguageCombo(ComboBox comboBox)
    {
        comboBox.Items.Clear();
        foreach (var choice in Choices)
        {
            comboBox.Items.Add(new ComboBoxItem
            {
                Content = choice.DisplayName,
                Tag = choice.Language
            });
        }

        comboBox.SelectedIndex = Math.Max(0, Choices.ToList().FindIndex(c => c.Language == StartupPreference));
    }

    private static UiLanguage Resolve(UiLanguage language)
    {
        return language == UiLanguage.Auto ? DetectOperatingSystemLanguage() : language;
    }

    private static UiLanguage LoadPreference()
    {
        try
        {
            if (File.Exists(SettingsPath) && Enum.TryParse<UiLanguage>(File.ReadAllText(SettingsPath).Trim(), out var language))
            {
                return language;
            }
        }
        catch
        {
            // Ignore invalid settings.
        }

        return UiLanguage.Auto;
    }

    private static void ApplyThreadCulture()
    {
        var cultureName = CurrentLanguage switch
        {
            UiLanguage.French => "fr-FR",
            UiLanguage.German => "de-DE",
            _ => "en-US"
        };

        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    private static void ApplyToObject(DependencyObject obj)
    {
        if (obj is Window window)
        {
            ApplyWindowTitle(window);
        }

        if (obj is ContentControl contentControl)
        {
            ApplyContent(contentControl);
        }

        if (obj is HeaderedContentControl headeredContentControl)
        {
            ApplyHeader(headeredContentControl);
        }

        if (obj is HeaderedItemsControl headeredItemsControl)
        {
            ApplyHeader(headeredItemsControl);
        }

        if (obj is TextBlock textBlock)
        {
            ApplyTextBlock(textBlock);
        }

        if (obj is DataGrid dataGrid)
        {
            foreach (var column in dataGrid.Columns)
            {
                ApplyDataGridColumn(column);
            }
        }

        if (obj is FrameworkElement element)
        {
            ApplyToolTip(element);
        }
    }

    private static void ApplyWindowTitle(Window window)
    {
        if (string.IsNullOrEmpty(window.Title))
        {
            return;
        }

        var original = Remember(window, OriginalTitleProperty, window.Title);
        window.Title = T(original);
    }

    private static void ApplyContent(ContentControl control)
    {
        if (control.Content is not string text || string.IsNullOrEmpty(text))
        {
            return;
        }

        var original = Remember(control, OriginalContentProperty, text);
        control.Content = T(original);
    }

    private static void ApplyHeader(HeaderedContentControl control)
    {
        if (control.Header is not string text || string.IsNullOrEmpty(text))
        {
            return;
        }

        var original = Remember(control, OriginalHeaderProperty, text);
        control.Header = T(original);
    }

    private static void ApplyHeader(HeaderedItemsControl control)
    {
        if (control.Header is not string text || string.IsNullOrEmpty(text))
        {
            return;
        }

        var original = Remember(control, OriginalHeaderProperty, text);
        control.Header = T(original);
    }

    private static void ApplyTextBlock(TextBlock textBlock)
    {
        if (BindingOperations.GetBindingExpression(textBlock, TextBlock.TextProperty) is not null)
        {
            return;
        }

        if (string.IsNullOrEmpty(textBlock.Text))
        {
            return;
        }

        var original = Remember(textBlock, OriginalTextProperty, textBlock.Text);
        textBlock.Text = T(original);
    }

    private static void ApplyDataGridColumn(DataGridColumn column)
    {
        if (column.Header is not string text || string.IsNullOrEmpty(text))
        {
            return;
        }

        var original = Remember(column, OriginalHeaderProperty, text);
        column.Header = T(original);
    }

    private static void ApplyToolTip(FrameworkElement element)
    {
        if (element.ToolTip is not string text || string.IsNullOrEmpty(text))
        {
            return;
        }

        var original = Remember(element, OriginalToolTipProperty, text);
        element.ToolTip = T(original);
    }

    private static string Remember(DependencyObject obj, DependencyProperty property, string currentValue)
    {
        if (obj.GetValue(property) is string original && !string.IsNullOrEmpty(original))
        {
            return original;
        }

        obj.SetValue(property, currentValue);
        return currentValue;
    }

    private static void TraverseLogicalChildren(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            ApplyToObject(child);
            TraverseLogicalChildren(child);
        }
    }

    private static void TraverseVisualChildren(DependencyObject root)
    {
        var count = 0;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(root);
        }
        catch
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            ApplyToObject(child);
            TraverseVisualChildren(child);
        }
    }

    private static void BuildDictionaries()
    {
        void Add(string fr, string en, string de)
        {
            English[fr] = en;
            German[fr] = de;
        }

        // Common / main window
        Add("Anno1800IslandTool v45.1 localisation", "Anno1800IslandTool v45.1 localization", "Anno1800IslandTool v45.1 Lokalisierung");
        Add("Ouvrir une archive ZIP", "Open ZIP archive", "ZIP-Archiv öffnen");
        Add("Revalider", "Validate again", "Erneut prüfen");
        Add("Exporter ZIP", "Export ZIP", "ZIP exportieren");
        Add("Sauver rapport", "Save report", "Bericht speichern");
        Add("Régénérer textes", "Regenerate texts", "Texte neu erzeugen");
        Add("Fenêtre vue 2D", "2D view window", "2D-Ansicht");
        Add("Fenêtre terrain 3D", "3D terrain window", "3D-Terrainfenster");
        Add("Éditeur matériaux", "Material editor", "Material-Editor");
        Add("Teinte végétation", "Vegetation tint", "Vegetationsfärbung");
        Add("Lire le détail des pages (plus lent)", "Read page details (slower)", "Seitendetails lesen (langsamer)");
        Add("Charge mesh/normalmaps en mémoire pour l'édition 3D complète.", "Loads mesh/normal maps into memory for full 3D editing.", "Lädt Meshes/Normalmaps für die vollständige 3D-Bearbeitung in den Speicher.");
        Add("RAM économe", "Save RAM", "RAM sparen");
        Add("v41.2 : lit les pages par lots et ne conserve pas le XML brut des pages mesh/normalmap. Décoche pour privilégier la vitesse/préservation brute.", "v41.2: reads pages in batches and does not keep raw XML for mesh/normal-map pages. Uncheck to favor speed/raw preservation.", "v41.2: liest Seiten in Blöcken und behält das Roh-XML der Mesh-/Normalmap-Seiten nicht. Deaktivieren für Geschwindigkeit/Rohdaten-Erhalt.");
        Add("v45.1 : localisation FR/EN/DE + lecture détail des pages activée par défaut.", "v45.1: FR/EN/DE localization + page details enabled by default.", "v45.1: FR/EN/DE-Lokalisierung + Seitendetails standardmäßig aktiv.");
        Add("Langue", "Language", "Sprache");
        Add("Résumé / log projet (zone redimensionnable)", "Project summary / log (resizable area)", "Projektzusammenfassung / Log (größenveränderbar)");
        Add("Fichiers", "Files", "Dateien");
        Add("Type", "Type", "Typ");
        Add("Taille", "Size", "Größe");
        Add("Chemin", "Path", "Pfad");
        Add("Pages terrain", "Terrain pages", "Terrain-Seiten");
        Add("Famille", "Family", "Familie");
        Add("Résumé", "Summary", "Zusammenfassung");
        Add("Matériaux", "Materials", "Materialien");
        Add("Texture", "Texture", "Textur");
        Add("Objet", "Object", "Objekt");
        Add("PropGrid (RD3D)", "PropGrid (RD3D)", "PropGrid (RD3D)");
        Add("FileNames", "File names", "Dateinamen");
        Add("FileName", "File name", "Dateiname");
        Add("Instances (&lt;None&gt;...&lt;/None&gt;)", "Instances (&lt;None&gt;...&lt;/None&gt;)", "Instanzen (&lt;None&gt;...&lt;/None&gt;)");
        Add("Raw &lt;None&gt; XML", "Raw &lt;None&gt; XML", "Rohes &lt;None&gt;-XML");
        Add("GroundObjects / végétation", "GroundObjects / vegetation", "GroundObjects / Vegetation");
        Add("Édition des objets de sol stockés dans les pages mesh : végétation, props et décorations. Attention : ce ne sont pas les PropGrid RD3D.", "Edit ground objects stored in mesh pages: vegetation, props and decorations. Note: these are not RD3D PropGrid items.", "Bearbeitung der in Mesh-Seiten gespeicherten Bodenobjekte: Vegetation, Props und Dekorationen. Achtung: Das sind keine RD3D-PropGrid-Objekte.");
        Add("Page X", "Page X", "Seite X");
        Add("Page Y", "Page Y", "Seite Y");
        Add("Cluster", "Cluster", "Cluster");
        Add("Kind", "Kind", "Art");
        Add("Rotation Y", "Rotation Y", "Rotation Y");
        Add("Coverage", "Coverage", "Deckung");
        Add("Page path", "Page path", "Seitenpfad");
        Add("GameData objects", "GameData objects", "GameData-Objekte");
        Add("Container", "Container", "Container");
        Add("Aucun projet chargé.", "No project loaded.", "Kein Projekt geladen.");
        Add("Folder", "Folder", "Ordner");
        Add("Position", "Position", "Position");
        Add("Slots (édition)", "Slots (editing)", "Slots (Bearbeitung)");
        Add("ObjectId", "ObjectId", "ObjectId");
        Add("Guid", "Guid", "Guid");
        Add("Zones (édition)", "Zones (editing)", "Zonen (Bearbeitung)");
        Add("PlayableArea L/T/R/B", "PlayableArea L/T/R/B", "PlayableArea L/O/R/U");
        Add("Area ID", "Area ID", "Area-ID");
        Add("Édition", "Editing", "Bearbeitung");
        Add("Nom interne", "Internal name", "Interner Name");
        Add("Synchroniser RD3D/GameData", "Synchronize RD3D/GameData", "RD3D/GameData synchronisieren");
        Add("ActiveMapGrid X / Y", "ActiveMapGrid X / Y", "ActiveMapGrid X / Y");
        Add("ActiveMapRect L / T / R / B", "ActiveMapRect L / T / R / B", "ActiveMapRect L / O / R / U");
        Add("MaterialSetFileName", "MaterialSetFileName", "MaterialSetFileName");
        Add("Laisse vide pour utiliser terra_01.xml", "Leave empty to use terra_01.xml", "Leer lassen, um terra_01.xml zu verwenden");
        Add("Appliquer métadonnées", "Apply metadata", "Metadaten anwenden");
        Add("Recharger champs", "Reload fields", "Felder neu laden");
        Add("Changer MapSize ne régénère pas les pages terrain. Utilise-le seulement pour des tests contrôlés.", "Changing MapSize does not regenerate terrain pages. Use it only for controlled tests.", "Eine Änderung von MapSize erzeugt Terrain-Seiten nicht neu. Nur für kontrollierte Tests verwenden.");
        Add("Vue 2D (édition)", "2D view (editing)", "2D-Ansicht (Bearbeitung)");
        Add("Preview", "Preview", "Vorschau");
        Add("Rafraîchir vue", "Refresh view", "Ansicht aktualisieren");
        Add("Appliquer vue 2D", "Apply 2D view", "2D-Ansicht anwenden");
        Add("Rafraîchir overlay auto", "Auto-refresh overlay", "Overlay automatisch aktualisieren");
        Add("Mode sélection", "Selection mode", "Auswahlmodus");
        Add("Déplacer slot", "Move slot", "Slot verschieben");
        Add("Déplacer spawn", "Move spawn", "Spawn verschieben");
        Add("Ajouter slot", "Add slot", "Slot hinzufügen");
        Add("Ajouter spawn", "Add spawn", "Spawn hinzufügen");
        Add("Déplacer GroundObject", "Move GroundObject", "GroundObject verschieben");
        Add("Ajouter GroundObject", "Add GroundObject", "GroundObject hinzufügen");
        Add("Zone coin HG", "Zone top-left", "Zone oben links");
        Add("Zone coin BD", "Zone bottom-right", "Zone unten rechts");
        Add("Quitter mode", "Exit mode", "Modus verlassen");
        Add("Mode: Sélection", "Mode: Selection", "Modus: Auswahl");
        Add("Coarse HeightMap", "Coarse HeightMap", "Grobe HeightMap");
        Add("Image / overlay d'édition", "Image / editing overlay", "Bild / Bearbeitungs-Overlay");
        Add("Sélectionne un slot ou un spawn dans les tableaux, puis utilise les modes ci-dessus. Un clic sur l'image déplace ou ajoute l'élément, ou définit les coins de la PlayableArea.", "Select a slot or spawn in the tables, then use the modes above. Left-clicking the image moves/adds the item or defines PlayableArea corners.", "Wähle einen Slot oder Spawn in den Tabellen und nutze dann die Modi oben. Ein Klick ins Bild verschiebt/fügt das Element hinzu oder setzt die Ecken der PlayableArea.");
        Add("Validation", "Validation", "Validierung");
        Add("Sévérité", "Severity", "Schweregrad");
        Add("Code", "Code", "Code");
        Add("Message", "Message", "Meldung");
        Add("Fichier", "File", "Datei");
        Add("Texte brut (édition)", "Raw text (editing)", "Rohtext (Bearbeitung)");
        Add("Appliquer texte", "Apply text", "Text anwenden");
        Add("Recharger texte", "Reload text", "Text neu laden");
        Add("Gameplay/rendu avancé", "Advanced gameplay/render", "Erweitertes Gameplay/Rendering");
        Add("Sélection", "Selection", "Auswahl");
        Add("Rapport texte", "Text report", "Textbericht");

        // Generic buttons used in several editors
        Add("Ajouter", "Add", "Hinzufügen");
        Add("Supprimer", "Delete", "Löschen");
        Add("Dupliquer", "Duplicate", "Duplizieren");
        Add("Appliquer", "Apply", "Anwenden");
        Add("Recharger", "Reload", "Neu laden");
        Add("Rafraîchir", "Refresh", "Aktualisieren");
        Add("Réinitialiser", "Reset", "Zurücksetzen");
        Add("Effacer", "Clear", "Leeren");
        Add("Vider instances", "Clear instances", "Instanzen leeren");
        Add("Appliquer PropGrid", "Apply PropGrid", "PropGrid anwenden");
        Add("Appliquer GameData", "Apply GameData", "GameData anwenden");
        Add("Appliquer GroundObjects", "Apply GroundObjects", "GroundObjects anwenden");

        // 3D editor
        Add("Terrain 3D Editor v42 mask-safe extension île", "Terrain 3D Editor v42 mask-safe island extension", "Terrain-3D-Editor v42 maskensichere Inselerweiterung");
        Add("Source 3D", "3D source", "3D-Quelle");
        Add("Mesh brut fusionné du LOD", "Raw merged LOD mesh", "Rohes zusammengeführtes LOD-Mesh");
        Add("Page mesh brute sélectionnée", "Selected raw mesh page", "Ausgewählte rohe Mesh-Seite");
        Add("Surface reconstruite lissée du LOD", "Smoothed reconstructed LOD surface", "Geglättete rekonstruierte LOD-Oberfläche");
        Add("Page reconstruite lissée", "Smoothed reconstructed page", "Geglättete rekonstruierte Seite");
        Add("Comparaison page brute / lissée / coarse", "Raw / smoothed / coarse page comparison", "Vergleich rohe/geglättete/grobe Seite");
        Add("Coarse dérivée des meshes (debug)", "Coarse derived from meshes (debug)", "Aus Meshes abgeleitete Coarse-Map (Debug)");
        Add("Relief", "Relief", "Relief");
        Add("Afficher bords de page", "Show page borders", "Seitengrenzen anzeigen");
        Add("Afficher points page sélectionnée", "Show selected page points", "Punkte der ausgewählten Seite anzeigen");
        Add("Afficher wireframe", "Show wireframe", "Drahtgitter anzeigen");
        Add("Afficher dirty region", "Show dirty region", "Dirty-Bereich anzeigen");
        Add("Ignorer pages vides", "Ignore empty pages", "Leere Seiten ignorieren");
        Add("Seulement pages décodées", "Decoded pages only", "Nur dekodierte Seiten");
        Add("Soudure inter-pages", "Inter-page welding", "Seitennähte verschweißen");
        Add("Reconstr.", "Reconstr.", "Rekonstr.");
        Add("X/Z uniques", "Unique X/Z", "Eindeutige X/Z");
        Add("Grille régulière", "Regular grid", "Regelmäßiges Raster");
        Add("Triangulation brute", "Raw triangulation", "Rohe Triangulation");
        Add("Outil", "Tool", "Werkzeug");
        Add("Navigation", "Navigation", "Navigation");
        Add("Raise", "Raise", "Anheben");
        Add("Lower", "Lower", "Absenken");
        Add("Smooth", "Smooth", "Glätten");
        Add("Flatten", "Flatten", "Einebnen");
        Add("Forme", "Shape", "Form");
        Add("Cercle", "Circle", "Kreis");
        Add("Carré", "Square", "Quadrat");
        Add("Rectangle", "Rectangle", "Rechteck");
        Add("Rayon", "Radius", "Radius");
        Add("Rect. L", "Rect. W", "Rekt. B");
        Add("Largeur complète du pinceau rectangle, en unités logiques de terrain.", "Full width of the rectangular brush, in logical terrain units.", "Volle Breite des Rechteckpinsels in logischen Terrain-Einheiten.");
        Add("Rect. H", "Rect. H", "Rekt. H");
        Add("Longueur complète du pinceau rectangle, en unités logiques de terrain.", "Full height of the rectangular brush, in logical terrain units.", "Volle Höhe des Rechteckpinsels in logischen Terrain-Einheiten.");
        Add("Intensité", "Intensity", "Intensität");
        Add("Falloff", "Falloff", "Falloff");
        Add("Linéaire", "Linear", "Linear");
        Add("Doux", "Soft", "Weich");
        Add("Plat", "Flat", "Flach");
        Add("Zone", "Zone", "Zone");
        Add("Monter", "Raise", "Anheben");
        Add("Descendre", "Lower", "Absenken");
        Add("Aplatir", "Flatten", "Einebnen");
        Add("Rayon 3D sur terrain", "3D radius on terrain", "3D-Radius auf Terrain");
        Add("Topologie source v39.1 (X/Z + indices du mesh édité)", "v39.1 source topology (X/Z + edited mesh indices)", "v39.1-Quelltopologie (X/Z + Indizes des bearbeiteten Meshs)");
        Add("Raw3..7 inchangés en v34", "Raw3..7 unchanged in v34", "Raw3..7 unverändert in v34");
        Add("Synchroniser tous les LOD mesh éditables", "Synchronize all editable mesh LODs", "Alle bearbeitbaren Mesh-LODs synchronisieren");
        Add("Forcer toutes les height/normal pages", "Force all height/normal pages", "Alle Height-/Normal-Seiten erzwingen");
        Add("Extension île hors mesh", "Island extension outside mesh", "Inselerweiterung außerhalb des Meshs");
        Add("Maj matériaux zones modifiées", "Update materials in modified zones", "Materialien in geänderten Zonen aktualisieren");
        Add("Cell.", "Cells", "Zellen");
        Add("Appliquer preset", "Apply preset", "Preset anwenden");
        Add("Preset Raw3..Raw7 : aucun", "Raw3..Raw7 preset: none", "Raw3..Raw7-Preset: keines");
        Add("Seuil Y", "Y threshold", "Y-Schwelle");
        Add("RD3D : hors terrain", "RD3D: outside terrain", "RD3D: außerhalb des Terrains");
        Add("Pages mesh", "Mesh pages", "Mesh-Seiten");
        Add("Filtres :", "Filters:", "Filter:");
        Add("LOD actif", "Active LOD", "Aktives LOD");
        Add("(grille = LOD sélectionné + filtres)", "(grid = selected LOD + filters)", "(Raster = ausgewähltes LOD + Filter)");
        Add("Statut", "Status", "Status");
        Add("Détails / vertex sample", "Details / vertex sample", "Details / Vertex-Beispiel");
        Add("Propagation / comparaison 3D / reconstruction", "Propagation / 3D comparison / reconstruction", "Propagation / 3D-Vergleich / Rekonstruktion");
        Add("Normal locale", "Local normal", "Lokale Normale");
        Add("Preview normalmap locale", "Local normal-map preview", "Lokale Normalmap-Vorschau");
        Add("Crop coarse", "Coarse crop", "Coarse-Ausschnitt");
        Add("Crop coarse de la page sélectionnée", "Coarse crop of selected page", "Coarse-Ausschnitt der ausgewählten Seite");
        Add("Page reconstruite", "Reconstructed page", "Rekonstruierte Seite");
        Add("Diff", "Diff", "Diff");
        Add("Diff coarse vs page reconstruite", "Coarse vs reconstructed page diff", "Diff Coarse vs. rekonstruierte Seite");

        // Heightmap editor
        Add("Terrain / Heightmap / Pages réelles v22", "Terrain / Heightmap / Real pages v22", "Terrain / Heightmap / reale Seiten v22");
        Add("Mode", "Mode", "Modus");
        Add("Force", "Force", "Stärke");
        Add("Afficher grille pages", "Show page grid", "Seitenraster anzeigen");
        Add("Afficher pinceau", "Show brush", "Pinsel anzeigen");
        Add("Afficher dirty mask", "Show dirty mask", "Dirty-Maske anzeigen");
        Add("Limiter le pinceau à la page sélectionnée", "Limit brush to selected page", "Pinsel auf ausgewählte Seite begrenzen");
        Add("Auto-sélection de page", "Auto-select page", "Seite automatisch auswählen");
        Add("Seulement éditables", "Editable only", "Nur bearbeitbare");
        Add("Stats", "Stats", "Statistik");
        Add("Régénération locale", "Local regeneration", "Lokale Regeneration");
        Add("Expansion pages", "Page expansion", "Seitenerweiterung");
        Add("Utiliser dirty bounds si disponibles", "Use dirty bounds if available", "Dirty-Grenzen verwenden, falls vorhanden");
        Add("Seulement la page sélectionnée", "Selected page only", "Nur ausgewählte Seite");
        Add("Height synthétique", "Synthetic height", "Synthetische Höhe");
        Add("Normal synthétique", "Synthetic normal", "Synthetische Normale");
        Add("Tint synthétique", "Synthetic tint", "Synthetische Tönung");
        Add("Comparaison mesh avant / après", "Mesh comparison before / after", "Mesh-Vergleich vorher/nachher");
        Add("Bounds", "Bounds", "Grenzen");
        Add("Page réelle sélectionnée", "Selected real page", "Ausgewählte reale Seite");

        // Material editors
        Add("Anno1800IslandTool v44.9 - Matériaux / vue agrandie", "Anno1800IslandTool v44.9 - Materials / enlarged view", "Anno1800IslandTool v44.9 - Materialien / vergrößerte Ansicht");
        Add("Anno1800IslandTool v44.9 - Hôte matériaux", "Anno1800IslandTool v44.9 - Material host", "Anno1800IslandTool v44.9 - Material-Host");
        Add("Vue agrandie matériaux", "Enlarged material view", "Vergrößerte Materialansicht");
        Add("Vue affichée", "Displayed view", "Angezeigte Ansicht");
        Add("La vue peinture modifie la carte. La vue seuils affiche une prévisualisation hauteur → matériaux.", "Paint view modifies the map. Threshold view previews height → materials.", "Die Malansicht ändert die Karte. Die Schwellenansicht zeigt Höhe → Materialien als Vorschau.");
        Add("Outils peinture matériaux", "Material painting tools", "Material-Malwerkzeuge");
        Add("Palette terrain (Type=0)", "Terrain palette (Type=0)", "Terrain-Palette (Typ=0)");
        Add("Inscrire ce materialset dans rd3d.xml / export", "Write this material set to rd3d.xml / export", "Dieses Materialset in rd3d.xml / Export eintragen");
        Add("Materialset courant : projet", "Current material set: project", "Aktuelles Materialset: Projekt");
        Add("Mode peinture activé", "Paint mode enabled", "Malmodus aktiviert");
        Add("Mode peinture activé : cliquer/glisser sur la carte peint le matériau sélectionné ou le pickup", "Paint mode enabled: click/drag on the map paints the selected material or pickup", "Malmodus aktiviert: Klicken/Ziehen auf der Karte malt das ausgewählte Material oder den Pickup");
        Add("Pickup : le prochain clic prélève la cellule", "Pickup: next click samples the cell", "Pickup: der nächste Klick nimmt die Zelle auf");
        Add("Pickup : le prochain clic prélève la cellule complète", "Pickup: next click samples the full cell", "Pickup: der nächste Klick nimmt die komplette Zelle auf");
        Add("Peindre avec la cellule pickup / cellule ci-dessous", "Paint with pickup cell / cell below", "Mit Pickup-Zelle / Zelle unten malen");
        Add("Peindre avec la cellule pickup (slots + alphas)", "Paint with pickup cell (slots + alphas)", "Mit Pickup-Zelle malen (Slots + Alphas)");
        Add("Source peinture", "Paint source", "Malquelle");
        Add("Matériau simple", "Single material", "Einzelmaterial");
        Add("Cellule terrain personnalisée", "Custom terrain cell", "Benutzerdefinierte Terrain-Zelle");
        Add("Créer / former une cellule terrain", "Create / shape a terrain cell", "Terrain-Zelle erstellen / formen");
        Add("Un pickup remplit ces slots. Tu peux ensuite modifier les IDs/alphas puis peindre avec cette cellule.", "A pickup fills these slots. You can then edit IDs/alphas and paint with this cell.", "Ein Pickup füllt diese Slots. Danach kannst du IDs/Alphas bearbeiten und mit dieser Zelle malen.");
        Add("Palette sélectionnée →", "Selected palette →", "Ausgewählte Palette →");
        Add("Cellule source : aucune", "Source cell: none", "Quellzelle: keine");
        Add("Slot", "Slot", "Slot");
        Add("Material ID", "Material ID", "Material-ID");
        Add("Alpha", "Alpha", "Alpha");
        Add("Cellule prête : fais un pickup ou saisis les slots.", "Cell ready: use pickup or enter slots.", "Zelle bereit: Pickup verwenden oder Slots eingeben.");
        Add("Utiliser cette cellule pour les seuils hauteur", "Use this cell for height thresholds", "Diese Zelle für Höhenschwellen verwenden");
        Add("Assigne la cellule éditée à une classe. L'outil hauteur → matériaux utilisera alors cette empreinte complète au lieu d'un simple ID.", "Assign the edited cell to a class. The height → materials tool will use this full footprint instead of a single ID.", "Weise die bearbeitete Zelle einer Klasse zu. Das Werkzeug Höhe → Materialien nutzt dann diesen kompletten Abdruck statt einer einzelnen ID.");
        Add("Eau profonde", "Deep water", "Tiefes Wasser");
        Add("Eau faible / corail", "Shallow water / coral", "Flachwasser / Koralle");
        Add("Eau faible", "Shallow water", "Flachwasser");
        Add("Plage", "Beach", "Strand");
        Add("Terre basse", "Low land", "Niedriges Land");
        Add("Roche pente", "Slope rock", "Hangfels");
        Add("Seuils : aucune cellule personnalisée assignée.", "Thresholds: no custom cell assigned.", "Schwellen: keine benutzerdefinierte Zelle zugewiesen.");
        Add("8 px", "8 px", "8 px");
        Add("Falloff doux", "Soft falloff", "Weicher Falloff");
        Add("Seuils hauteur monde", "World-height thresholds", "Welthöhen-Schwellen");
        Add("Matériaux de transition", "Transition materials", "Übergangsmaterialien");
        Add("Seuils", "Thresholds", "Schwellen");
        Add("Eau profonde ≤", "Deep water ≤", "Tiefes Wasser ≤");
        Add("Ligne eau ≤", "Water line ≤", "Wasserlinie ≤");
        Add("Plage ≤", "Beach ≤", "Strand ≤");
        Add("Terre basse ≤", "Low land ≤", "Niedriges Land ≤");
        Add("Pente roche ≥", "Rock slope ≥", "Felshang ≥");
        Add("Force transition", "Transition force", "Übergangsstärke");
        Add("Peindre la roche sur pentes fortes", "Paint rock on steep slopes", "Fels auf steilen Hängen malen");
        Add("Mélanger au lieu de remplacer dominant", "Blend instead of replacing dominant", "Mischen statt dominantes Material ersetzen");
        Add("Utiliser empreintes cellule vanilla", "Use vanilla cell footprints", "Vanilla-Zellabdrücke verwenden");
        Add("Peint des combinaisons MaterialIDs2 + MaterialAlphas2 complètes au lieu d'un seul ID; recommandé contre les carrés gris.", "Paints complete MaterialIDs2 + MaterialAlphas2 combinations instead of a single ID; recommended against gray squares.", "Malt komplette MaterialIDs2 + MaterialAlphas2-Kombinationen statt einer einzelnen ID; empfohlen gegen graue Quadrate.");
        Add("Vue seuils prête.", "Threshold view ready.", "Schwellenansicht bereit.");
        Add("Zoom vue", "View zoom", "Ansichtszoom");
        Add("Astuce : Ctrl + molette dans la carte ajuste aussi le zoom.", "Tip: Ctrl + mouse wheel in the map also adjusts zoom.", "Tipp: Strg + Mausrad in der Karte passt ebenfalls den Zoom an.");
        Add("Pickup : aucun. Clic droit sur la carte pour prélever.", "Pickup: none. Right-click the map to sample.", "Pickup: keiner. Rechtsklick auf die Karte zum Aufnehmen.");
        Add("Prêt. Le cercle rouge s'affiche seulement en mode peinture, dans la vue modifiable.", "Ready. The red circle is shown only in paint mode, in the editable view.", "Bereit. Der rote Kreis erscheint nur im Malmodus in der bearbeitbaren Ansicht.");
        Add("Cellule / matériau", "Cell / material", "Zelle / Material");
        Add("Clique pour inspecter. Clic droit ou mode Pickup pour prélever en vue peinture. Cliquer/glisser peint quand le mode peinture est actif.", "Click to inspect. Right-click or Pickup mode samples in paint view. Click/drag paints when paint mode is active.", "Klicken zum Prüfen. Rechtsklick oder Pickup-Modus nimmt in der Malansicht auf. Klicken/Ziehen malt bei aktivem Malmodus.");
        Add("Vue / peinture MaterialIDs2 + MaterialAlphas2. Les changements restent dans une copie de travail jusqu'au bouton Appliquer.", "View / paint MaterialIDs2 + MaterialAlphas2. Changes stay in a working copy until Apply.", "MaterialIDs2 + MaterialAlphas2 anzeigen/malen. Änderungen bleiben bis zum Anwenden in einer Arbeitskopie.");
        Add("v25 - Transition hauteur → matériau", "v25 - Height → material transition", "v25 - Höhe → Material-Übergang");
        Add("Remplit eau profonde / eau faible / plage / terre basse depuis la heightmap GameData, sans modifier la hauteur.", "Fills deep water / shallow water / beach / low land from the GameData heightmap without changing height.", "Füllt Tiefwasser / Flachwasser / Strand / niedriges Land aus der GameData-Heightmap, ohne die Höhe zu ändern.");
        Add("Application", "Application", "Anwendung");
        Add("Force 1 à 31", "Force 1 to 31", "Stärke 1 bis 31");
        Add("Rayon 1 à 64 px", "Radius 1 to 64 px", "Radius 1 bis 64 px");
        Add("Prêt : règle les matériaux et seuils puis applique à la copie de travail.", "Ready: set materials and thresholds, then apply to the working copy.", "Bereit: Materialien und Schwellen einstellen, dann auf die Arbeitskopie anwenden.");
        Add("Répartition des matériaux dominants", "Dominant material distribution", "Verteilung dominanter Materialien");
        Add("Couleur", "Color", "Farbe");
        Add("Pixels", "Pixels", "Pixel");
        Add("Texture / objet", "Texture / object", "Textur / Objekt");
        Add("Cellule / matériau sélectionné", "Selected cell / material", "Ausgewählte Zelle / Material");


        // Remaining button text nodes
        Add("Ouvrir l’éditeur material map", "Open material-map editor", "Materialmap-Editor öffnen");
        Add("Tout supprimer", "Delete all", "Alle löschen");
        Add("Appliquer slots", "Apply slots", "Slots anwenden");
        Add("Appliquer zones", "Apply zones", "Zonen anwenden");
        Add("Ajouter area", "Add area", "Area hinzufügen");
        Add("Supprimer area", "Delete area", "Area löschen");
        Add("Supprimer slot", "Delete slot", "Slot löschen");
        Add("Supprimer spawn", "Delete spawn", "Spawn löschen");
        Add("Appliquer au projet", "Apply to project", "Auf Projekt anwenden");
        Add("Déplacer GD obj", "Move GD object", "GD-Objekt verschieben");
        Add("Déplacer GD", "Move GD", "GD verschieben");
        Add("Appliquer GD", "Apply GD", "GD anwenden");
        Add("Vue haut", "Top view", "Draufsicht");
        Add("Vue iso", "Iso view", "Iso-Ansicht");
        Add("Cadrer scène", "Frame scene", "Szene einpassen");
        Add("Cadrer sélection", "Frame selection", "Auswahl einpassen");
        Add("Valider LOD mesh courant", "Validate current mesh LOD", "Aktuelles Mesh-LOD validieren");
        Add("Régénérer normalmaps", "Regenerate normal maps", "Normalmaps neu erzeugen");
        Add("Régénérer tintmaps", "Regenerate tint maps", "Tintmaps neu erzeugen");
        Add("Valider tout le terrain", "Validate all terrain", "Gesamtes Terrain validieren");
        Add("Piquer Raw3..Raw7", "Pick Raw3..Raw7", "Raw3..Raw7 aufnehmen");
        Add("Vérif dirty", "Check dirty", "Dirty prüfen");
        Add("Appliquer hauteur", "Apply height", "Höhe anwenden");
        Add("Relever la page", "Raise page", "Seite anheben");
        Add("Abaisser la page", "Lower page", "Seite absenken");
        Add("Lisser la page", "Smooth page", "Seite glätten");
        Add("Aplatir la page", "Flatten page", "Seite einebnen");
        Add("Régénérer dérivés locaux", "Regenerate local derivatives", "Lokale Ableitungen neu erzeugen");
        Add("Écrire NormalMaps interprétés", "Write interpreted NormalMaps", "Interpretierte NormalMaps schreiben");
        Add("Écrire Mesh pages locales", "Write local mesh pages", "Lokale Mesh-Seiten schreiben");
        Add("Exporter patch ciblé", "Export targeted patch", "Gezielten Patch exportieren");
        Add("Charger materialset...", "Load material set...", "Materialset laden...");
        Add("Ouvrir vue agrandie...", "Open enlarged view...", "Vergrößerte Ansicht öffnen...");
        Add("Mettre l'ID", "Set ID", "ID setzen");
        Add("Utiliser cette cellule", "Use this cell", "Diese Zelle verwenden");
        Add("Vider", "Clear", "Leeren");
        Add("Assigner au seuil", "Assign to threshold", "Schwelle zuweisen");
        Add("Vider seuils", "Clear thresholds", "Schwellen leeren");
        Add("Afficher la vue seuils", "Show threshold view", "Schwellenansicht anzeigen");
        Add("Mettre à jour image peinture depuis seuils", "Update paint image from thresholds", "Malbild aus Schwellen aktualisieren");
        Add("Réinitialiser la copie", "Reset copy", "Kopie zurücksetzen");
        Add("Rafraîchir la vue", "Refresh view", "Ansicht aktualisieren");
        Add("Rafraîchir stats", "Refresh stats", "Statistik aktualisieren");
        Add("Effacer pickup", "Clear pickup", "Pickup leeren");
        Add("Prévisualiser zones", "Preview zones", "Zonen voranzeigen");
        Add("Appliquer anti-rouge global", "Apply global anti-red", "Globales Anti-Rot anwenden");
        Add("Recharger depuis le projet", "Reload from project", "Aus Projekt neu laden");

        // 2D editor
        Add("Vue 2D - slots / zones", "2D view - slots / zones", "2D-Ansicht - Slots / Zonen");
        Add("Filtres d'affichage", "Display filters", "Anzeigefilter");
        Add("ActiveMapRect", "ActiveMapRect", "ActiveMapRect");
        Add("PlayableArea", "PlayableArea", "PlayableArea");
        Add("Slots", "Slots", "Slots");
        Add("Spawns", "Spawns", "Spawns");
        Add("PlayableArea L / T / R / B", "PlayableArea L / T / R / B", "PlayableArea L / O / R / U");
        Add("Infos", "Info", "Info");
        Add("Astuce : garde le clic droit pour déplacer la vue. Utilise Ctrl + molette pour zoomer/dézoomer. Le clic gauche sert à sélectionner/déplacer selon le mode.", "Tip: hold right-click to pan. Use Ctrl + wheel to zoom in/out. Left-click selects/moves depending on the mode.", "Tipp: Rechtsklick halten, um die Ansicht zu verschieben. Strg + Mausrad zoomt. Linksklick wählt/verschiebt je nach Modus.");

        // Global tint base editor
        Add("Anno1800IslandTool v44.8 - Éditeur teinte végétation / BaseTextureData", "Anno1800IslandTool v44.8 - Vegetation tint / BaseTextureData editor", "Anno1800IslandTool v44.8 - Editor für Vegetationstönung / BaseTextureData");
        Add("TintMapLevels/BaseTextureData", "TintMapLevels/BaseTextureData", "TintMapLevels/BaseTextureData");
        Add("Cette map globale est échantillonnée par les props/végétation via cTerrainTintTex. Elle influence la luminosité/couleur de la végétation.", "This global map is sampled by props/vegetation through cTerrainTintTex. It affects vegetation brightness/color.", "Diese globale Map wird von Props/Vegetation über cTerrainTintTex abgetastet. Sie beeinflusst Helligkeit/Farbe der Vegetation.");
        Add("Outil pinceau", "Brush tool", "Pinselwerkzeug");
        Add("Anti-rouge / garder orange max", "Anti-red / keep orange max", "Anti-Rot / maximal Orange");
        Add("Verdir", "Make greener", "Grüner machen");
        Add("Oranger doux", "Soft orange", "Weiches Orange");
        Add("Éclaircir", "Lighten", "Aufhellen");
        Add("Assombrir", "Darken", "Abdunkeln");
        Add("Désaturer", "Desaturate", "Entsättigen");
        Add("Neutraliser chroma", "Neutralize chroma", "Chroma neutralisieren");
        Add("16 px", "16 px", "16 px");
        Add("Important : après application, l'export préservera cette BaseTextureData manuelle au lieu de la régénérer automatiquement.", "Important: after applying, export will preserve this manual BaseTextureData instead of regenerating it automatically.", "Wichtig: Nach dem Anwenden bewahrt der Export diese manuelle BaseTextureData, statt sie automatisch neu zu erzeugen.");
        Add("Zoom", "Zoom", "Zoom");
        Add("Aucune map chargée.", "No map loaded.", "Keine Map geladen.");

        // Common runtime messages and dialogs
        Add("Aucune archive chargée.", "No archive loaded.", "Kein Archiv geladen.");
        Add("Aucune sélection.", "No selection.", "Keine Auswahl.");
        Add("Aucun projet chargé.", "No project loaded.", "Kein Projekt geladen.");
        Add("Erreur d'import", "Import error", "Importfehler");
        Add("Erreur d'export", "Export error", "Exportfehler");
        Add("Export terminé", "Export complete", "Export abgeschlossen");
        Add("Sauvegarde", "Save", "Speichern");
        Add("Rapport sauvegardé.", "Report saved.", "Bericht gespeichert.");
        Add("Régénération", "Regeneration", "Regeneration");
        Add("Les textes pivots ont été régénérés depuis le modèle.", "Pivot texts have been regenerated from the model.", "Pivot-Texte wurden aus dem Modell neu erzeugt.");
        Add("Vue 2D", "2D view", "2D-Ansicht");
        Add("Vue terrain 3D", "3D terrain view", "3D-Terrainansicht");
        Add("Aucun gamedata.xml chargé.", "No gamedata.xml loaded.", "Keine gamedata.xml geladen.");
        Add("Erreur lecture GameData objects.", "Error reading GameData objects.", "Fehler beim Lesen der GameData-Objekte.");
        Add("Sélectionner une archive d'île interprétée", "Select an interpreted island archive", "Interpretiertes Inselarchiv auswählen");
        Add("Exporter l'archive modifiée", "Export modified archive", "Geändertes Archiv exportieren");
        Add("Archives ZIP (*.zip)|*.zip|Tous les fichiers (*.*)|*.*", "ZIP archives (*.zip)|*.zip|All files (*.*)|*.*", "ZIP-Archive (*.zip)|*.zip|Alle Dateien (*.*)|*.*");
        Add("Archives ZIP (*.zip)|*.zip", "ZIP archives (*.zip)|*.zip", "ZIP-Archive (*.zip)|*.zip");
        AddV4639Translations();
    }
}
