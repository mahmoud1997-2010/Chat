﻿using AutoMapper;
using Chat.Web.Data;
using Chat.Web.Models;
using Chat.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Chat.Web.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        public readonly static List<UserViewModel> _Connections = new List<UserViewModel>();
        public readonly static List<RoomViewModel> _Rooms = new List<RoomViewModel>();
        private readonly static Dictionary<string, string> _ConnectionsMap = new Dictionary<string, string>();

        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;

        public ChatHub(ApplicationDbContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        public async Task CreateRoom(string roomName)
        {
            try
            {

                // Accept: Letters, numbers and one space between words.
                Match match = Regex.Match(roomName, @"^\w+( \w+)*$");   //Regular expression must match
                if (!match.Success)
                {
                    await Clients.Caller.SendAsync("onError", "Invalid room name!\nRoom name must contain only letters and numbers.");
                }
                else if (roomName.Length < 5 || roomName.Length > 100)
                {
                    await Clients.Caller.SendAsync("onError", "Room name must be between 5-100 characters!");
                }
                else if (_context.Rooms.Any(r => r.Name == roomName))
                {
                    await Clients.Caller.SendAsync("onError", "Another chat room with this name exists");
                }
                else
                {
                    // Create and save chat room in database
                    var user = _context.Users.Where(u => u.UserName == IdentityName).FirstOrDefault();
                    var room = new Room()
                    {
                        Name = roomName,
                        Admin = user
                    };
                    _context.Rooms.Add(room);
                    _context.SaveChanges();

                    if (room != null)
                    {
                        // Update room list
                        var roomViewModel = _mapper.Map<Room, RoomViewModel>(room);
                        _Rooms.Add(roomViewModel);
                        await Clients.All.SendAsync("addChatRoom", roomViewModel);    //To show room to all User 
                    }
                }
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("onError", "Couldn't create chat room: " + ex.Message);
            }
        }

        public async Task SendToRoom(string roomName, string message)
        {
            try
            {
                var user = _context.Users.Where(u => u.UserName == IdentityName).FirstOrDefault();
                var room = _context.Rooms.Where(r => r.Name == roomName).FirstOrDefault();

                if (!string.IsNullOrEmpty(message.Trim()))                       //remove white spaces character from message 
                {
                    // Create and save message in database
                    var msg = new Message()
                    {
                        Content = Regex.Replace(message, @"(?i)<(?!img|a|/a|/img).*?>", string.Empty),   //to remove html tags
                        FromUser = user,
                        ToRoom = room,
                        Timestamp = DateTime.Now
                    };
                    _context.Messages.Add(msg);
                    _context.SaveChanges();

                    // Broadcast the message
                    var messageViewModel = _mapper.Map<Message, MessageViewModel>(msg);     //chat.js recieve this message to send to it
                    await Clients.Group(roomName).SendAsync("newMessage", messageViewModel);    //used to send message on group of Room and users listen (Group For Users)
                    //Clients => mean Connected Clients
                }
            }
            catch (Exception)
            {
                await Clients.Caller.SendAsync("onError", "Message not send! Message should be 1-500 characters.");
            }
        }

        public async Task Join(string roomName)   //Join Group of Room Not RoomItself
        {
            try
            {
                var user = _Connections.Where(u => u.Username == IdentityName).FirstOrDefault();
                if (user != null && user.CurrentRoom != roomName)
                {
                    //// Remove user from others list as if it is not on this room (Deactive)
                    if (!string.IsNullOrEmpty(user.CurrentRoom))
                        await Clients.OthersInGroup(user.CurrentRoom).SendAsync("removeUser", user);

                    ////// Join to new chat room (active)
                    await Leave(user.CurrentRoom);  //to leave GroupChat from current room old(Remove Connection From Group)
                await Groups.AddToGroupAsync(Context.ConnectionId, roomName);  //show in this Room that turn to it add His Name To This Group

                user.CurrentRoom = roomName;

                    // Tell others to update their list of users
                    await Clients.Groups(roomName).SendAsync("addUser", user);
                }
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("onError", "You failed to join the chat room!" + ex.Message);
            }
        }

        public async Task Leave(string roomName)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomName);    //when change room or logout
        }

        

        public async Task DeleteRoom(string roomName)
        {
            try
            {
                // Delete from database
                var room = _context.Rooms.Include(r => r.Admin)
                    .Where(r => r.Name == roomName && r.Admin.UserName == IdentityName).FirstOrDefault();
                _context.Rooms.Remove(room);
                _context.SaveChanges();

                // Delete from list
                var roomViewModel = _Rooms.First(r => r.Name == roomName);
                _Rooms.Remove(roomViewModel);

                // Move users back to Lobby
                await Clients.Group(roomName).SendAsync("onRoomDeleted", string.Format("Room {0} has been deleted.\nYou are now moved to the Lobby!", roomName));

                // Tell all users to update their room list
                await Clients.All.SendAsync("removeChatRoom", roomViewModel);
            }
            catch (Exception)
            {
                await Clients.Caller.SendAsync("onError", "Can't delete this chat room. Only owner can delete this room.");
            }
        }

        public IEnumerable<RoomViewModel> GetRooms()
        {
            // First run?
            if (_Rooms.Count == 0)
            {
                foreach (var room in _context.Rooms)
                {
                    var roomViewModel = _mapper.Map<Room, RoomViewModel>(room);
                    _Rooms.Add(roomViewModel);
                }
            }

            return _Rooms.ToList();
        }

        public IEnumerable<UserViewModel> GetUsers(string roomName)
        {
            return _Connections.Where(u => u.CurrentRoom == roomName).ToList();
        }

        public IEnumerable<MessageViewModel> GetMessageHistory(string roomName)
        {
            var messageHistory = _context.Messages.Where(m => m.ToRoom.Name == roomName)
                    .Include(m => m.FromUser)
                    .Include(m => m.ToRoom)
                    .OrderByDescending(m => m.Timestamp)
                    .Take(20)
                    .AsEnumerable()
                    .Reverse()
                    .ToList();

            return _mapper.Map<IEnumerable<Message>, IEnumerable<MessageViewModel>>(messageHistory);
        }

        public override Task OnConnectedAsync()
        {
            try
            {
                var user = _context.Users.Where(u => u.UserName == IdentityName).FirstOrDefault();
                var userViewModel = _mapper.Map<ApplicationUser, UserViewModel>(user);
                userViewModel.Device = GetDevice();
                userViewModel.CurrentRoom = "";

                if (!_Connections.Any(u => u.Username == IdentityName))
                {
                    _Connections.Add(userViewModel);
                    _ConnectionsMap.Add(IdentityName, Context.ConnectionId);
                }

                Clients.Caller.SendAsync("getProfileInfo", user.FullName, user.Avatar);
            }
            catch (Exception ex)
            {
                Clients.Caller.SendAsync("onError", "OnConnected:" + ex.Message);
            }
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            try
            {
                var user = _Connections.Where(u => u.Username == IdentityName).First();
                _Connections.Remove(user);

                // Tell other users to remove you from their list
                Clients.OthersInGroup(user.CurrentRoom).SendAsync("removeUser", user);

                // Remove mapping
                _ConnectionsMap.Remove(user.Username);
            }
            catch (Exception ex)
            {
                Clients.Caller.SendAsync("onError", "OnDisconnected: " + ex.Message);
            }

            return base.OnDisconnectedAsync(exception);
        }

        private string IdentityName
        {
            get { return Context.User.Identity.Name; }
        }

        private string GetDevice()
        {
            var device = Context.GetHttpContext().Request.Headers["Device"].ToString();
            if (!string.IsNullOrEmpty(device) && (device.Equals("Desktop") || device.Equals("Mobile")))
                return device;

            return "Web";
        }
    }
}
