using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KanbanContracts;

namespace Curs.Services;

public sealed class BoardService(ApiClient api)
{
    public async Task<IReadOnlyList<BoardSummaryDto>> ListAsync(CancellationToken token) =>
        await api.SendAsync<List<BoardSummaryDto>>(HttpMethod.Get, "api/boards", cancellationToken: token) ?? [];
    public async Task<BoardDetailsDto> GetAsync(Guid id, CancellationToken token) =>
        await api.SendAsync<BoardDetailsDto>(HttpMethod.Get, $"api/boards/{id}", cancellationToken: token) ?? throw new InvalidOperationException("Немає даних дошки.");
    public async Task<BoardDetailsDto> CreateAsync(CreateBoardRequest request, CancellationToken token) =>
        await api.SendAsync<BoardDetailsDto>(HttpMethod.Post, "api/boards", request, token) ?? throw new InvalidOperationException("Немає даних дошки.");
    public Task<BoardDetailsDto?> UpdateAsync(Guid id, UpdateBoardRequest request, CancellationToken token) =>
        api.SendAsync<BoardDetailsDto>(HttpMethod.Put, $"api/boards/{id}", request, token);
    public Task<object?> DeleteAsync(Guid id, string version, CancellationToken token) =>
        api.SendAsync<object>(HttpMethod.Delete, $"api/boards/{id}" + VersionQuery(version), cancellationToken: token);
    public async Task<IReadOnlyList<MemberDto>> MembersAsync(Guid id, CancellationToken token) =>
        await api.SendAsync<List<MemberDto>>(HttpMethod.Get, $"api/boards/{id}/members", cancellationToken: token) ?? [];
    public Task<MemberDto?> AddMemberAsync(Guid id, AddMemberRequest request, CancellationToken token) =>
        api.SendAsync<MemberDto>(HttpMethod.Post, $"api/boards/{id}/members", request, token);
    public Task<MemberDto?> PermissionsAsync(Guid id, Guid userId, UpdateMemberPermissionsRequest request, CancellationToken token) =>
        api.SendAsync<MemberDto>(HttpMethod.Patch, $"api/boards/{id}/members/{userId}", request, token);
    public Task<object?> RemoveMemberAsync(Guid id, Guid userId, string version, CancellationToken token) =>
        api.SendAsync<object>(HttpMethod.Delete, $"api/boards/{id}/members/{userId}" + VersionQuery(version), cancellationToken: token);
    private static string VersionQuery(string version) => "?expectedBoardVersion=" + Uri.EscapeDataString(version);
}
