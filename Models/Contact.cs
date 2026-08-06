using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SuperDoc.Sms.Models;

/// <summary>A person in the address book, matched to messages by <see cref="PhoneKey"/>.</summary>
public sealed class Contact : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _familyName = string.Empty;
    private string _givenName = string.Empty;
    private string _address = string.Empty;
    private string _note = string.Empty;
    private string _phoneNumber = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; set; }

    /// <summary>Danh xưng - the form of address that precedes the name (Anh, Chị, Ông, BS., TS.).</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (SetField(ref _title, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>Họ - the family name.</summary>
    public string FamilyName
    {
        get => _familyName;
        set
        {
            if (SetField(ref _familyName, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>Tên - the given name.</summary>
    public string GivenName
    {
        get => _givenName;
        set
        {
            if (SetField(ref _givenName, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>Địa chỉ.</summary>
    public string Address
    {
        get => _address;
        set => SetField(ref _address, value);
    }

    public string Note
    {
        get => _note;
        set => SetField(ref _note, value);
    }

    /// <summary>As the user typed it; shown in the contact editor.</summary>
    public string PhoneNumber
    {
        get => _phoneNumber;
        set
        {
            if (SetField(ref _phoneNumber, value))
            {
                OnPropertyChanged(nameof(PhoneKey));
                OnPropertyChanged(nameof(DisplayPhone));
            }
        }
    }

    /// <summary>Normalised form used to match messages; not shown.</summary>
    public string PhoneKey => Models.PhoneNumber.ToKey(PhoneNumber);

    public string DisplayPhone => Models.PhoneNumber.ToDisplay(PhoneNumber);

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Vietnamese name order: title, then family name, then given name. Falls back to whichever
    /// part exists, and finally to the number, so a half-filled contact still shows something
    /// usable rather than an empty row.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var full = string.Join(
                ' ',
                new[] { Title, FamilyName, GivenName }.Where(p => !string.IsNullOrWhiteSpace(p)));

            return full.Length > 0 ? full : DisplayPhone;
        }
    }

    public Contact Clone() => new()
    {
        Id = Id,
        Title = Title,
        FamilyName = FamilyName,
        GivenName = GivenName,
        Address = Address,
        Note = Note,
        PhoneNumber = PhoneNumber,
        CreatedAt = CreatedAt
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
