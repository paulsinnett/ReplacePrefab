using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System;
using System.Reflection;

public class ReplaceWithPrefabWindow : EditorWindow
{
    public GameObject prefab;

    public class ObjectReference
    {
        SerializedProperty property;

        public ObjectReference(SerializedProperty property)
        {
            this.property = property.Copy();
        }
        
        public void RemapToObject(GameObject gameObject)
        {
            UnityEngine.Object unityObject = gameObject;
            Type type = property.objectReferenceValue.GetType();
            if (type != typeof(GameObject))
            {
                unityObject = gameObject.GetComponent(type);
            }
            if (unityObject != null)
            {
                // Debug.LogFormat(
                //     property.serializedObject.targetObject,
                //     "Replace property {0} component type {1} reference to object {2} with reference to object {3}",
                //     property.propertyPath,
                //     type.ToString(),
                //     property.objectReferenceValue.name,
                //     unityObject.name);
                property.serializedObject.UpdateIfRequiredOrScript();
                property.objectReferenceValue = unityObject;
                property.serializedObject.ApplyModifiedProperties();
            }
        }
    }

    [MenuItem("GameObject/Replace with prefab")]
    static void Init()
    {
        // Get existing open window or if none, make a new one:
        ReplaceWithPrefabWindow window = (ReplaceWithPrefabWindow)EditorWindow.GetWindow(typeof(ReplaceWithPrefabWindow));
        window.titleContent = new GUIContent("Replace with prefab");
        window.Show();
    }

    List<GameObject> GetChildObjects(GameObject parent)
    {
        List<GameObject> children = new List<GameObject>();
        for (int i = 0; i < parent.transform.childCount; ++i)
        {
            children.Add(parent.transform.GetChild(i).gameObject);
        }
        return children;
    }

    List<GameObject> GetFlattenedHierarchy(List<GameObject> objectList)
    {
        List<GameObject> flattenedList = new List<GameObject>();
        foreach (GameObject entry in objectList)
        {
            flattenedList.Add(entry);
            flattenedList.AddRange(GetFlattenedHierarchy(GetChildObjects(entry)));
        }
        return flattenedList;
    }

    // return a dictionary of instance ID's to each object and all of its children
    Dictionary<int, GameObject> FindObjectReferenceLookupTable(List<GameObject> objectList)
    {
        Dictionary<int, GameObject> lookupTable = new Dictionary<int, GameObject>();
        foreach (GameObject entry in GetFlattenedHierarchy(objectList))
        {
            if (!lookupTable.ContainsKey(entry.GetInstanceID()))
            {
                lookupTable.Add(entry.GetInstanceID(), entry);
            }
        }

        return lookupTable;
    }

    bool IsReference(FieldInfo field)
    {
        return field.FieldType.IsSubclassOf(typeof(UnityEngine.Object));
    }

    Dictionary<int, List<ObjectReference>> objectReferences;
    void ClearObjectReferences()
    {
        objectReferences = new Dictionary<int, List<ObjectReference>>();
    }

    void AddObjectReference(int instanceID, ObjectReference reference)
    {
        List<ObjectReference> objectReferencesList = new List<ObjectReference>();
        if (objectReferences.ContainsKey(instanceID))
        {
            objectReferencesList = objectReferences[instanceID];
        }
        objectReferencesList.Add(reference);
        if (!objectReferences.ContainsKey(instanceID))
        {
            objectReferences.Add(instanceID, objectReferencesList);
        }
    }

    GameObject GetChildByName(GameObject parent, string name)
    {
        List<GameObject> children = GetChildObjects(parent);
        return children.Find((child) => child.name == name);
    }

    void CopyStates(GameObject original, GameObject copy)
    {
        List<GameObject> copyChildren = GetChildObjects(copy);
        foreach (GameObject copyChild in copyChildren)
        {
            GameObject correspondingChild = GetChildByName(original, copyChild.name);
            if (correspondingChild != null)
            {
                CopyStates(correspondingChild, copyChild);
            }
        }
        Undo.RecordObject(copy, "copy object state");
        copy.SetActive(original.activeSelf);
    }

    void MergeChildren(GameObject original, GameObject copy)
    {
        List<GameObject> copyChildren = GetChildObjects(copy);
        List<GameObject> originalChildren = GetChildObjects(original);

        foreach (GameObject originalChild in originalChildren)
        {
            GameObject correspondingChild = copyChildren.Find((copyChild) => copyChild.name == originalChild.name);
            if (correspondingChild != null)
            {
                MergeChildren(originalChild, correspondingChild);
            }
            else
            {
                Undo.SetTransformParent(originalChild.transform, copy.transform, "adopt child");
            }
        }
    }

    void OnGUI()
    {
        GUILayout.Label("Replacement prefab", EditorStyles.boldLabel);
        prefab = (GameObject)EditorGUILayout.ObjectField(prefab, typeof(GameObject), false);
        if (prefab != null && GUILayout.Button("Replace Selection"))
        {
            var selectedObjectsLookup = FindObjectReferenceLookupTable(new List<GameObject>(Selection.gameObjects));
            ClearObjectReferences();
            Scene scene = EditorSceneManager.GetActiveScene();
            foreach (GameObject sceneObject in scene.GetRootGameObjects())
            {
                foreach (var component in sceneObject.GetComponentsInChildren(typeof(Component), true))
                {
                    if (!component.GetType().IsSubclassOf(typeof(Transform)) && component.GetType() != typeof(Transform))
                    {
                        SerializedObject serializedObject = new SerializedObject(component);
                        SerializedProperty property = serializedObject.GetIterator();
                        do
                        {
                            if (property.name != "m_GameObject" && property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null)
                            {
                                Type type = property.objectReferenceValue.GetType();
                                int reference = property.objectReferenceValue.GetInstanceID();
                                GameObject gameObject = null;
                                if (type == typeof(GameObject))
                                {
                                    gameObject = (GameObject)property.objectReferenceValue;
                                }
                                else if (type.IsSubclassOf(typeof(Component)))
                                {
                                    Component referencedComponent = (Component)property.objectReferenceValue;
                                    reference = referencedComponent.gameObject.GetInstanceID();
                                    gameObject = referencedComponent.gameObject;
                                }
                                if (selectedObjectsLookup.ContainsKey(reference))
                                {
                                    AddObjectReference(reference, new ObjectReference(property));
                                }
                            }
                        }
                        while (property.Next(true));
                    }
                }
            }
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Undo replace objects with prefab");
            foreach (var selectedObject in Selection.gameObjects)
            {
                GameObject replacement = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                Undo.RegisterCreatedObjectUndo(replacement, "replacement prefab");
                Undo.SetTransformParent(replacement.transform, selectedObject.transform.parent, "set parent");
                replacement.name = selectedObject.name;
                CopyTransform(selectedObject, replacement);
                CopyStates(selectedObject, replacement);
                MergeChildren(selectedObject, replacement);
                int siblingNumber = selectedObject.transform.GetSiblingIndex();
                if (objectReferences.ContainsKey(selectedObject.GetInstanceID()))
                {
                    foreach (var externalReference in objectReferences[selectedObject.GetInstanceID()])
                    {
                        externalReference.RemapToObject(replacement.gameObject);
                    }
                }
                Undo.DestroyObjectImmediate(selectedObject);
                replacement.transform.SetSiblingIndex(siblingNumber);
            }
            Undo.CollapseUndoOperations(group);
        }
    }

    static void CopyTransform(GameObject original, GameObject replacement)
    {
        RectTransform originalRectTransform = original.GetComponent<RectTransform>();
        RectTransform replacementRectTransform = replacement.GetComponent<RectTransform>();
        Undo.RecordObject(replacement, "copy transform");
        if (originalRectTransform != null && replacementRectTransform != null)
        {
            replacementRectTransform.anchoredPosition = originalRectTransform.anchoredPosition;
            replacementRectTransform.sizeDelta = originalRectTransform.sizeDelta;
            replacementRectTransform.localScale = originalRectTransform.localScale;
        }
        else
        {
            replacement.transform.position = original.transform.position;
            replacement.transform.rotation = original.transform.rotation;
            replacement.transform.localScale = original.transform.localScale;
        }
    }
}
