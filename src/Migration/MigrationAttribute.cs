using System;

namespace eQuantic.Core.Data.Migration;

/// <summary>
/// Marks a class as a data migration and records its title and timestamp.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class MigrationAttribute : Attribute
{
    /// <summary>
    /// Gets the migration title.
    /// </summary>
    public string Title { get; private set; }

    /// <summary>
    /// Gets the migration timestamp.
    /// </summary>
    public DateTime Date { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationAttribute"/> class.
    /// </summary>
    /// <param name="title">The migration title.</param>
    /// <param name="year">The year component of the migration timestamp.</param>
    /// <param name="month">The month component of the migration timestamp.</param>
    /// <param name="day">The day component of the migration timestamp.</param>
    /// <param name="hour">The hour component of the migration timestamp.</param>
    /// <param name="minute">The minute component of the migration timestamp.</param>
    /// <param name="second">The second component of the migration timestamp.</param>
    public MigrationAttribute(string title, int year, int month, int day, int hour, int minute, int second)
    {
        Title = title;
        Date = new DateTime(year, month, day, hour, minute, second);
    }
}
