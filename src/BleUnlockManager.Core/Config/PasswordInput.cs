namespace BleUnlockManager.Core.Config;

/// <summary>
/// Campo de contraseña del formulario. La contraseña guardada nunca entra en el control: el campo
/// queda vacío (con un texto indicativo) y lo que el usuario escribe la sustituye. Así el botón
/// «ver contraseña» solo puede mostrar lo recién escrito. Campo vacío = conservar la guardada.
/// </summary>
public sealed class PasswordInput
{
    /// <summary>Contraseña guardada (descifrada), solo en memoria del modelo de vista.</summary>
    public string Stored { get; private set; } = "";

    /// <summary>Lo escrito en el campo.</summary>
    public string Typed { get; private set; } = "";

    public bool HasStored => Stored.Length > 0;

    public bool CanReveal => Typed.Length > 0;

    /// <summary>Valor que se guarda: lo escrito, o la guardada si el campo está vacío.</summary>
    public string Effective => Typed.Length > 0 ? Typed : Stored;

    /// <summary>Valor leído del disco o recién guardado: el campo vuelve a quedar vacío.</summary>
    public void Load(string stored)
    {
        Stored = stored;
        Typed = "";
    }

    public void Edit(string typed) => Typed = typed;
}
