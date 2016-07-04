package putked;

import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.HashMap;

import javafx.beans.property.ReadOnlyStringWrapper;
import javafx.beans.value.ChangeListener;
import javafx.beans.value.ObservableValue;
import javafx.collections.FXCollections;
import javafx.collections.ObservableList;
import javafx.collections.transformation.FilteredList;
import javafx.event.EventHandler;
import javafx.scene.Node;
import javafx.scene.Parent;
import javafx.scene.control.ContextMenu;
import javafx.scene.control.MenuItem;
import javafx.scene.control.TableColumn;
import javafx.scene.control.TableColumn.CellDataFeatures;
import javafx.scene.control.TableRow;
import javafx.scene.control.TableView;
import javafx.scene.control.TextField;
import javafx.scene.control.TreeItem;
import javafx.scene.control.TreeView;
import javafx.scene.input.ClipboardContent;
import javafx.scene.input.Dragboard;
import javafx.scene.input.MouseEvent;
import javafx.scene.input.TransferMode;
import javafx.scene.layout.HBox;
import javafx.scene.layout.Priority;
import javafx.scene.layout.VBox;
import javafx.util.Callback;
import putki.Compiler;;

public class ObjectLibrary {
	public class ObjEntry {
		public String name;
		public String path;
		public Compiler.ParsedStruct type;
	};

	class DirEntry {
		public String name;
		public String path;
		private ArrayList<ObjEntry> entries;
	};

	private HBox m_root;
	private TextField m_search;
	private TreeView<String> m_dirView;
	private TableView<ObjEntry> m_filesView;
	private HashMap<TreeItem<String>, DirEntry> m_dirMap = new HashMap<>();
	private ObservableList<ObjEntry> m_allObjects = FXCollections.observableArrayList();

	FilteredList<ObjEntry> m_filteredData;
	private String m_dirFilterString = "";

	ObjectLibrary() {

		m_root = new HBox();
		m_root.setFillHeight(true);
		m_root.setMaxWidth(100000.0);
		m_root.setMaxHeight(100000.0);

		// Creating a tree table view
		m_dirView = new TreeView<String>();

		m_root.getChildren().add(m_dirView);

		TableColumn<ObjEntry, String> col_fn = new TableColumn<>("Name");
		TableColumn<ObjEntry, String> col_type = new TableColumn<>("Type");

		col_fn.setPrefWidth(300);

		col_fn.setCellValueFactory(new Callback<CellDataFeatures<ObjEntry, String>, ObservableValue<String>>() {
			public ObservableValue<String> call(
				CellDataFeatures<ObjEntry, String> p) {
					return new ReadOnlyStringWrapper(p.getValue().path);
			}
		});

		col_type.setCellValueFactory(new Callback<CellDataFeatures<ObjEntry, String>, ObservableValue<String>>() {
			public ObservableValue<String> call(CellDataFeatures<ObjEntry, String> p) {
				Compiler.ParsedStruct t = p.getValue().type;
				if (t != null)
					return new ReadOnlyStringWrapper(t.name);
				return new ReadOnlyStringWrapper("<NULL>");
			}
		});

		m_filesView = new TableView<ObjEntry>();
		m_filesView.getColumns().add(0, col_fn);
		m_filesView.getColumns().add(1, col_type);

		VBox fbox = new VBox();
		m_search = new TextField();
		m_search.setMaxWidth(Double.MAX_VALUE);
		m_search.textProperty().addListener(new ChangeListener<String>() {
			@Override
			public void changed(
					final ObservableValue<? extends String> observable,
					final String oldValue, final String newValue) {
				updateFilter();
			}
		});
		fbox.getChildren().setAll(m_search, m_filesView);

		m_root.getChildren().add(fbox);
		HBox.setHgrow(fbox, Priority.ALWAYS);

		m_filteredData = new FilteredList<ObjEntry>(m_allObjects, p -> true);
		m_filesView.setItems(m_filteredData);

		m_dirView.getSelectionModel().selectedItemProperty()
				.addListener(new ChangeListener<TreeItem<String>>() {
					@Override
					public void changed(
							ObservableValue<? extends TreeItem<String>> paramObservableValue,
							TreeItem<String> paramT1,
							TreeItem<String> selectedItem) {
						DirEntry de = m_dirMap.get(selectedItem);
						if (de != null)
						{
							m_dirFilterString = de.path;
							updateFilter();
							System.out.println("I got " + de.entries.size()
									+ " for " + de.name);
						}
					}
				});

		m_filesView.setOnMousePressed(new EventHandler<MouseEvent>() {
			@Override
			public void handle(MouseEvent event) {
				if (event.isPrimaryButtonDown() && event.getClickCount() == 2) {
					Main.s_instance.startEditing(m_filesView.getSelectionModel().getSelectedItem().path);
				}
			}
		});

		ContextMenu dirmenu = new ContextMenu();
		ArrayList<DataImporter> importers = Main.getImporters();
		for (DataImporter imp : importers)
		{
			MenuItem newobj = new MenuItem("Import [" + imp.getName() + "]");
			newobj.setOnAction( (actionEvt) -> {
				TreeItem<String> ti = m_dirView.getSelectionModel().getSelectedItem();
				String whereTo = m_dirMap.get(ti).path;
				javafx.application.Platform.runLater(new Runnable() {
					@Override
					public void run() {
						if (imp.importTo(whereTo))
							loadIndex();
					}
				});
				actionEvt.consume();
			});
			dirmenu.getItems().add(newobj);
		}

		MenuItem newobj = new MenuItem("New object");
		newobj.setOnAction( (actionEvt) -> {
			TreeItem<String> ti = m_dirView.getSelectionModel().getSelectedItem();
			String whereTo = m_dirMap.get(ti).path + "neue";
			javafx.application.Platform.runLater(new Runnable() {
				@Override
				public void run() {
					Compiler.ParsedStruct t = Main.s_instance.askForType();
					if (t != null)
					{
						Main.ImportFinalizationQuestion fin = new Main.ImportFinalizationQuestion();
						fin.proposedPath = whereTo;
						Main.s_instance.askImportFinalization(fin, null);
						if (fin.accepted)
						{
							DataObject obj = new DataObject(t, fin.proposedPath);
							Main.s_dataWriter.WriteObject(obj);
							loadIndex();
						}
					}
				}
			});
			actionEvt.consume();
		});

		dirmenu.getItems().add(newobj);

		m_dirView.setContextMenu(dirmenu);

		m_filesView.setRowFactory(new Callback<TableView<ObjEntry>, TableRow<ObjEntry>>() {
			@Override
			public TableRow<ObjEntry> call(TableView<ObjEntry> tableView) {
		        return new TableRow<ObjEntry>() {
		            @Override
		            public void updateItem(ObjEntry item, boolean empty) {
		                super.updateItem(item, empty);
		                if (item != null) {
		                	this.setContextMenu(makeContextMenu(item));
		                } else {
		                	this.setContextMenu(null);
		                }
		                Node th = this;
		                setOnDragDetected(new EventHandler<MouseEvent>() {
		                    public void handle(MouseEvent event) {
		                        Dragboard db = th.startDragAndDrop(TransferMode.ANY);
		                        ClipboardContent content = new ClipboardContent();
		                        content.putString(item.path);
		                        db.setContent(content);
		                        event.consume();
		                    }
		                });

		            }
		        };
			}
		});

		loadIndex();
	}

	public ContextMenu makeContextMenu(ObjEntry item)
	{
    	ContextMenu mn = new ContextMenu();
    	for (Editor e:Main.getEditors())
    	{
    		if (e.canEdit(item.type))
    		{
    			MenuItem mi = new MenuItem(e.getName());
    			mi.setOnAction( (actionEvt) -> {
    				Main.s_instance.startEditing(item.path,  e);
    			});
    			mn.getItems().add(mi);
    		}
    	}
    	return mn;
	}

	public ObservableList<ObjEntry> getAllObjects() {
		return m_allObjects;
	}

	private void updateFilter() {
		m_filteredData.setPredicate(obj -> {
			String s = m_search.getText();
			if (obj.path.startsWith(m_dirFilterString))
				return s.isEmpty() || obj.path.contains(s)
						|| obj.type.name.contains(s);
			return false;
		});
	}

	private void loadIndex()
	{
		m_dirMap.clear();
		m_allObjects.clear();
		final TreeItem<String> root = new TreeItem<>("/");
		root.setExpanded(true);
		System.out.println("Obj paths is " + Main.s_instance.getObjsPath());
		scanDirectory(root, Main.s_instance.getObjsPath().toFile(), "");
		m_dirView.setRoot(root);
	}

	private void scanDirectory(TreeItem<String> n, File f, String path) {
		if (!f.exists()) {
			System.out.println("File " + f.getName() + " does not exist!");
			return;
		}

		File[] files = f.listFiles();
		if (files == null) {
			System.out.println("File " + f.getName() + " is not a directory!");
			return;
		}

		// this directory.
		DirEntry de = new DirEntry();
		de.entries = new ArrayList<>();
		de.path = path;

		ArrayList<TreeItem<String>> out = new ArrayList<>();
		for (int i = 0; i < files.length; i++) {
			if (files[i].isDirectory()) {
				if (!files[i].getName().equals(".")
						&& !files[i].getName().equals("..")) {
					TreeItem<String> ni = new TreeItem<>(files[i].getName());
					out.add(ni);
					scanDirectory(ni, files[i], path + files[i].getName() + "/");
				}
			} else {
				String ending = ".json";
				String name = files[i].getName();
				if (name.length() < ending.length())
					continue;
				if (!name.endsWith(ending))
					continue;

				Compiler.ParsedStruct objType = null;

				try {
					byte[] scan = new byte[64];
					InputStream is = new FileInputStream(files[i]);
					int bread = is.read(scan);

					if (bread > 0) {
						String matchPrefix = "type: \"";
						String s = new String(scan);
						int where = s.indexOf(matchPrefix);
						if (where > 0)
						{
							s = s.substring(where + matchPrefix.length());
							int end = s.indexOf('"');
							if (end > 0)
							{
								s = s.substring(0, end);
								objType = Main.s_compiler.getTypeByName(s);
							}
						}
					}
					is.close();
				}
				catch (IOException e)
				{
				}

				ObjEntry oe = new ObjEntry();
				oe.name = name.substring(0, name.length() - ending.length());
				oe.path = path + oe.name;

				if (objType != null) {
					oe.type = objType;
				} else {
					System.out.println("Slow-path loading [" + oe.path + "]");
					DataObject obj = Main.s_dataLoader.load(oe.path);
					if (obj != null)
						oe.type = obj.getType();
				}

				if (oe.type != null) {
					de.entries.add(oe);
					m_allObjects.add(oe);
				}
			}
		}

		m_dirMap.put(n, de);

		n.getChildren().setAll(out);
	}

	public Parent getRoot() {
		return m_root;
	}
}
