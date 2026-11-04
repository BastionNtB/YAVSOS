namespace YAVSOS.Core.Interface;

/// <summary>
/// What stashes items in this game: YAVSOS's own stash, or AzuAutoStore when it is installed (D042). P10-04: a sentence
/// that names the stasher is one message per stasher, chosen by this value, never by an English word put into it.
/// </summary>
public enum Stasher { Yavsos, AzuAutoStore }
