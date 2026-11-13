using System.Data;
using System.Text.RegularExpressions;
using BetaGiaiPhong.Data.Entities;
using BetaGiaiPhong.Data.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BetaGiaiPhong.Data.Services;

/// <summary>
/// Physical room maintenance. The room and its seats change in one database transaction.
/// A room with showtime history cannot be physically deleted or have its seat layout changed.
/// </summary>
public sealed class RoomManagementService(BetaDbContext db, ISystemClock clock)
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "2D", "3D", "IMAX", "IMAX_3D", "4DX", "SCREENX", "DOLBY_ATMOS", "VIP"
    };

    private static string ValidateType(string? value)
    {
        var type = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (!AllowedTypes.Contains(type))
            throw new BusinessRuleException("INVALID_ROOM_TYPE", "Loại phòng chiếu không được hỗ trợ.");
        return type;
    }

    private static string ValidateName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length is < 1 or > 100)
            throw new BusinessRuleException("INVALID_ROOM_NAME", "Tên phòng phải có 1–100 ký tự.");
        return name;
    }

    private static void ValidateLayout(int rows, short columns, int vipStartRow)
    {
        if (rows is < 1 or > 20 || columns is < 1 or > 25 || rows * columns > 500)
            throw new BusinessRuleException("INVALID_SEAT_LAYOUT", "Sơ đồ ghế yêu cầu 1–20 hàng, 1–25 ghế/hàng và tối đa 500 ghế.");
        if (vipStartRow < 1 || vipStartRow > rows + 1)
            throw new BusinessRuleException("INVALID_VIP_ROW", "Hàng VIP phải từ 1 đến số hàng + 1.");
    }

    private static void AddGrid(BetaDbContext context, long roomId, int rows, short columns, int vipStartRow)
    {
        for (int r = 0; r < rows; r++)
        {
            var rowLabel = ((char)('A' + r)).ToString();
            var seatType = r + 1 >= vipStartRow ? "VIP" : "STANDARD";
            for (short col = 1; col <= columns; col++)
            {
                context.Seats.Add(new Seat
                {
                    RoomId = roomId,
                    RowLabel = rowLabel,
                    ColumnNo = col,
                    SeatCode = rowLabel + col,
                    SeatTypeCode = seatType,
                    IsActive = true
                });
            }
        }
    }

    private static bool GridMatches(ICollection<Seat> current, int rows, short columns, int vipStartRow)
    {
        if (current.Count != rows * columns) return false;
        var codes = current.ToDictionary(s => s.SeatCode, StringComparer.Ordinal);
        for (int r = 0; r < rows; r++)
        {
            var label = ((char)('A' + r)).ToString();
            var expectedType = r + 1 >= vipStartRow ? "VIP" : "STANDARD";
            for (short col = 1; col <= columns; col++)
            {
                if (!codes.TryGetValue(label + col, out var seat) ||
                    seat.RowLabel != label || seat.ColumnNo != col ||
                    seat.SeatTypeCode != expectedType || !seat.IsActive)
                    return false;
            }
        }
        return true;
    }

    public async Task<long> CreateAsync(
        long managerId,
        string roomCode,
        string roomName,
        string roomType,
        int rows,
        short columns,
        int vipStartRow,
        CancellationToken ct = default)
    {
        var code = (roomCode ?? string.Empty).Trim().ToUpperInvariant();
        var name = ValidateName(roomName);
        var type = ValidateType(roomType);
        if (!Regex.IsMatch(code, @"^[A-Z0-9_]{2,24}$"))
            throw new BusinessRuleException("INVALID_ROOM_CODE", "Mã phòng phải có 2–24 ký tự chữ, số hoặc gạch dưới.");
        ValidateLayout(rows, columns, vipStartRow);

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await SecurityRules.EnsureRoleAsync(db, managerId, ct, "MANAGER", "ADMIN");
            if (await db.Rooms.AnyAsync(r => r.RoomCode == code, ct))
                throw new BusinessRuleException("ROOM_CODE_EXISTS", $"Mã phòng '{code}' đã tồn tại.");

            var requiredTypes = vipStartRow <= rows ? new[] { "STANDARD", "VIP" } : new[] { "STANDARD" };
            var activeTypes = await db.SeatTypes.AsNoTracking().Where(t => t.IsActive)
                .Select(t => t.SeatTypeCode).ToListAsync(ct);
            if (requiredTypes.Except(activeTypes).Any())
                throw new BusinessRuleException("SEAT_TYPES_NOT_READY", "Thiếu loại ghế STANDARD/VIP đang hoạt động.");

            var room = new Room { RoomCode = code, RoomName = name, RoomType = type, Status = "ACTIVE" };
            db.Rooms.Add(room);
            await db.SaveChangesAsync(ct);
            AddGrid(db, room.RoomId, rows, columns, vipStartRow);
            db.AuditLogs.Add(Auditing.Event(managerId, "ROOM_CREATED", "Room", room.RoomId.ToString(),
                new { roomCode = code, roomName = name, roomType = type, rows, columns, vipStartRow }, clock.UtcNow));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return room.RoomId;
        }
        catch (DbUpdateException ex) when (ex.GetBaseException() is SqlException sql &&
                                           (sql.Number == 2601 || sql.Number == 2627))
        {
            throw new BusinessRuleException("ROOM_CODE_EXISTS", $"Mã phòng '{code}' đã tồn tại.");
        }
    }

    public async Task UpdateAsync(
        long managerId,
        long roomId,
        string roomName,
        string roomType,
        string status,
        int? rows,
        short? columns,
        int? vipStartRow,
        CancellationToken ct = default)
    {
        var name = ValidateName(roomName);
        var type = ValidateType(roomType);
        var statusCode = (status ?? string.Empty).Trim().ToUpperInvariant();
        if (statusCode is not ("ACTIVE" or "INACTIVE"))
            throw new BusinessRuleException("INVALID_ROOM_STATUS", "Trạng thái phòng phải là ACTIVE hoặc INACTIVE.");

        bool editGrid = rows.HasValue || columns.HasValue || vipStartRow.HasValue;
        if (editGrid)
        {
            if (!(rows.HasValue && columns.HasValue && vipStartRow.HasValue))
                throw new BusinessRuleException("INVALID_SEAT_LAYOUT", "Phải cung cấp đầy đủ hàng, cột và hàng bắt đầu VIP.");
            ValidateLayout(rows.Value, columns.Value, vipStartRow.Value);
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await SecurityRules.EnsureRoleAsync(db, managerId, ct, "MANAGER", "ADMIN");
        if (!await SqlServerLocks.RoomAsync(db, roomId, ct))
            throw new BusinessRuleException("ROOM_NOT_FOUND", "Không tìm thấy phòng chiếu.");

        var room = await db.Rooms.Include(r => r.Seats).SingleAsync(r => r.RoomId == roomId, ct);
        bool hasShowtimes = await db.Showtimes.AnyAsync(s => s.RoomId == roomId, ct);
        if (editGrid && hasShowtimes)
            throw new BusinessRuleException("ROOM_LAYOUT_LOCKED", "Phòng đã có suất chiếu; không thể thay đổi sơ đồ ghế để bảo toàn lịch sử vé.");

        if (statusCode == "INACTIVE" && room.Status != "INACTIVE" &&
            await db.Showtimes.AnyAsync(s => s.RoomId == roomId && s.Status != "CANCELLED" &&
                                            s.EndUtc > clock.UtcNow, ct))
            throw new BusinessRuleException("ROOM_HAS_FUTURE_SHOWTIMES",
                "Phòng còn suất chiếu chưa kết thúc. Hãy hủy/đóng lịch phù hợp trước khi ngừng hoạt động.");

        var oldDetails = new { room.RoomName, room.RoomType, room.Status, SeatCount = room.Seats.Count };
        room.RoomName = name;
        room.RoomType = type;
        room.Status = statusCode;

        if (editGrid && !GridMatches(room.Seats, rows!.Value, columns!.Value, vipStartRow!.Value))
        {
            // No showtime history implies no ShowtimeSeats / OrderSeats tied to this room.
            db.Seats.RemoveRange(room.Seats);
            await db.SaveChangesAsync(ct); // remove old unique (room_id, seat_code) keys first
            AddGrid(db, roomId, rows.Value, columns.Value, vipStartRow.Value);
        }

        db.AuditLogs.Add(Auditing.Event(managerId, "ROOM_UPDATED", "Room", roomId.ToString(),
            new { before = oldDetails, after = new { roomName = name, roomType = type, status = statusCode },
                  layoutUpdated = editGrid, rows, columns, vipStartRow }, clock.UtcNow));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task DeleteAsync(long managerId, long roomId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await SecurityRules.EnsureRoleAsync(db, managerId, ct, "MANAGER", "ADMIN");
        if (!await SqlServerLocks.RoomAsync(db, roomId, ct))
            throw new BusinessRuleException("ROOM_NOT_FOUND", "Không tìm thấy phòng chiếu.");

        if (await db.Showtimes.AnyAsync(s => s.RoomId == roomId, ct))
            throw new BusinessRuleException("ROOM_IN_USE",
                "Phòng đã có lịch sử suất chiếu và không được xóa vĩnh viễn. Hãy ngừng hoạt động phòng.");

        var room = await db.Rooms.Include(r => r.Seats).SingleAsync(r => r.RoomId == roomId, ct);
        var oldCode = room.RoomCode;
        var oldName = room.RoomName;
        var seatCount = room.Seats.Count;
        db.Seats.RemoveRange(room.Seats);
        db.Rooms.Remove(room);
        db.AuditLogs.Add(Auditing.Event(managerId, "ROOM_DELETED", "Room", roomId.ToString(),
            new { roomCode = oldCode, roomName = oldName, seatCount }, clock.UtcNow));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
