using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UserRole.Services;
using UserRoles.Data;
using UserRoles.Helpers;
using UserRoles.Models;
using UserRoles.ViewModels;

namespace UserRoles.Controllers
{
    [Authorize]
    public class AdminController : Controller
    {
        private readonly AppDbContext _context;
        private readonly UserManager<Users> _userManager;
        private readonly IEmailServices _emailService;
        private readonly ILogger<AdminController> _logger;

        public AdminController(
            AppDbContext context,
            UserManager<Users> userManager,
            IEmailServices emailService,
            ILogger<AdminController> logger)
        {
            _context = context;
            _userManager = userManager;
            _emailService = emailService;
            _logger = logger;
        }

        // Dashboard with statistics
        public async Task<IActionResult> Dashboard()
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var viewModel = new AdminDashboardViewModel
            {
                TotalStudents = await _context.Enrollments.CountAsync(e => e.Status == "approved"),
                PendingEnrollments = await _context.Enrollments.CountAsync(e => e.Status == "pending"),
                TotalProfessors = await _context.Users.CountAsync(u => u.Role == "professor"),
                TotalSections = 48, // 8 sections per grade × 6 grades

                // Students per grade level
                StudentsPerGradeLevel = await _context.Enrollments
                    .Where(e => e.Status == "approved")
                    .GroupBy(e => e.GradeLevel)
                    .Select(g => new GradeLevelStats
                    {
                        GradeLevel = g.Key,
                        StudentCount = g.Count()
                    })
                    .OrderBy(g => g.GradeLevel)
                    .ToListAsync(),

                // Professors per grade level
                ProfessorsPerGradeLevel = await _context.Users
                    .Where(u => u.Role == "professor")
                    .GroupBy(u => u.AssignedGradeLevel)
                    .Select(g => new GradeLevelStats
                    {
                        GradeLevel = g.Key,
                        ProfessorCount = g.Count()
                    })
                    .OrderBy(g => g.GradeLevel)
                    .ToListAsync(),

                // Recent enrollments
                RecentEnrollments = await _context.Enrollments
                    .Include(e => e.User)
                    .OrderByDescending(e => e.EnrollmentDate)
                    .Take(5)
                    .ToListAsync()
            };

            return View(viewModel);
        }

        // View all students
        public async Task<IActionResult> ViewStudents(string status = "all")
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            IQueryable<Enrollment> query = _context.Enrollments.Include(e => e.User);

            if (status != "all")
            {
                query = query.Where(e => e.Status == status);
            }

            var students = await query.OrderBy(e => e.EnrollmentDate).ToListAsync();
            ViewBag.CurrentFilter = status;

            return View(students);
        }

        // Accept enrollment
        // Accept enrollment
        // GET: Show enrollment form with subject selection
        [HttpGet]
        public async Task<IActionResult> EnrollStudent(int id)
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var enrollment = await _context.Enrollments
                .Include(e => e.User)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (enrollment == null)
            {
                return NotFound();
            }

            if (enrollment.Status != "pending")
            {
                TempData["Error"] = "This enrollment has already been processed.";
                return RedirectToAction(nameof(ViewStudents));
            }

            var gradeLevel = int.Parse(enrollment.GradeLevel);
            var viewModel = new EnrollStudentViewModel
            {
                EnrollmentId = enrollment.Id,
                StudentName = enrollment.StudentName,
                GradeLevel = enrollment.GradeLevel,
                Section = enrollment.Section ?? 0,
                ParentName = enrollment.ParentName,
                ContactNumber = enrollment.ContactNumber,
                Address = enrollment.Address
            };

            // Get available professors based on grade level
            if (gradeLevel >= 1 && gradeLevel <= 3)
            {
                // Grades 1-3: Get the single professor for this grade
                var professor = await _context.Users
                    .FirstOrDefaultAsync(u => u.Role == "professor" &&
                                             u.AssignedGradeLevel == enrollment.GradeLevel);

                viewModel.AvailableProfessors["All Subjects"] = new List<ProfessorOption>();
                if (professor != null)
                {
                    viewModel.AvailableProfessors["All Subjects"].Add(new ProfessorOption
                    {
                        Id = professor.Id,
                        Name = professor.FullName,
                        Subject = "All Subjects"
                    });
                }
            }
            else if (gradeLevel >= 4 && gradeLevel <= 6)
            {
                // Grades 4-6: Get professors for each subject in this section
                var subjects = new[] { "Math", "Science", "English", "Filipino", "Social Studies", "MAPEH" };

                foreach (var subject in subjects)
                {
                    var professors = await _context.Users
                        .Where(u => u.Role == "professor" &&
                                   u.AssignedGradeLevel == enrollment.GradeLevel &&
                                   u.AssignedSection == enrollment.Section &&
                                   u.AssignedSubject == subject)
                        .Select(u => new ProfessorOption
                        {
                            Id = u.Id,
                            Name = u.FullName,
                            Subject = subject
                        })
                        .ToListAsync();

                    // Also check ProfessorSectionAssignments
                    var additionalProfessors = await _context.ProfessorSectionAssignments
                        .Where(a => a.GradeLevel == enrollment.GradeLevel &&
                                   a.Section == enrollment.Section &&
                                   a.Subject == subject)
                        .Select(a => new ProfessorOption
                        {
                            Id = a.ProfessorId,
                            Name = a.Professor.FullName,
                            Subject = subject
                        })
                        .ToListAsync();

                    var allProfessors = professors.Union(additionalProfessors)
                        .GroupBy(p => p.Id)
                        .Select(g => g.First())
                        .ToList();

                    viewModel.AvailableProfessors[subject] = allProfessors;
                    viewModel.SubjectProfessors[subject] = "";
                }
            }

            return View(viewModel);
        }

        // POST: Process enrollment with subject assignments
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnrollStudent(EnrollStudentViewModel model)
        {
            var enrollment = await _context.Enrollments
                .Include(e => e.User)
                .FirstOrDefaultAsync(e => e.Id == model.EnrollmentId);

            if (enrollment == null)
            {
                return NotFound();
            }

            var gradeLevel = int.Parse(enrollment.GradeLevel);

            // Validate professor assignments
            if (gradeLevel >= 1 && gradeLevel <= 3)
            {
                if (string.IsNullOrEmpty(model.SingleProfessorId))
                {
                    TempData["Error"] = "Please select a professor for this student.";
                    return RedirectToAction(nameof(EnrollStudent), new { id = model.EnrollmentId });
                }
            }
            else if (gradeLevel >= 4 && gradeLevel <= 6)
            {
                var subjects = new[] { "Math", "Science", "English", "Filipino", "Social Studies", "MAPEH" };
                foreach (var subject in subjects)
                {
                    if (!model.SubjectProfessors.ContainsKey(subject) ||
                        string.IsNullOrEmpty(model.SubjectProfessors[subject]))
                    {
                        TempData["Error"] = $"Please select a professor for {subject}.";
                        return RedirectToAction(nameof(EnrollStudent), new { id = model.EnrollmentId });
                    }
                }
            }

            // Update enrollment status
            enrollment.Status = "approved";
            await _context.SaveChangesAsync();

            // Create subject enrollments
            if (gradeLevel >= 1 && gradeLevel <= 3)
            {
                var subjectEnrollment = new SubjectEnrollment
                {
                    EnrollmentId = enrollment.Id,
                    Subject = "All Subjects",
                    ProfessorId = model.SingleProfessorId,
                    EnrolledDate = DateTime.UtcNow
                };
                _context.SubjectEnrollments.Add(subjectEnrollment);
            }
            else if (gradeLevel >= 4 && gradeLevel <= 6)
            {
                foreach (var subjectProf in model.SubjectProfessors)
                {
                    var subjectEnrollment = new SubjectEnrollment
                    {
                        EnrollmentId = enrollment.Id,
                        Subject = subjectProf.Key,
                        ProfessorId = subjectProf.Value,
                        EnrolledDate = DateTime.UtcNow
                    };
                    _context.SubjectEnrollments.Add(subjectEnrollment);
                }
            }

            await _context.SaveChangesAsync();

            // Get assigned professors with their schedules
            var assignedProfessors = await _context.SubjectEnrollments
                .Where(se => se.EnrollmentId == enrollment.Id)
                .Include(se => se.Professor)
                .ToListAsync();

            // Get professor schedule details from ProfessorSectionAssignments
            var professorSchedules = await _context.ProfessorSectionAssignments
                .Where(psa => psa.GradeLevel == enrollment.GradeLevel &&
                             psa.Section == enrollment.Section.Value)
                .Include(psa => psa.Professor)
                .ToListAsync();

            // Get room assignment
            string assignedRoom = RoomAssignmentHelper.GetRoomForSection(enrollment.GradeLevel, enrollment.Section.Value);

            // Get class schedule
            bool isMorningShift = gradeLevel % 2 == 0;
            string shift = isMorningShift ? "Morning" : "Afternoon";
            string classSchedule = isMorningShift ? "7:00 AM - 12:00 PM" : "1:00 PM - 6:00 PM";

            // Build detailed schedule HTML
            string scheduleHtml = BuildScheduleEmailHtml(enrollment, assignedProfessors, professorSchedules,
                                                          assignedRoom, classSchedule, shift);

            // Send acceptance email
            try
            {
                string subject = "🎉 Enrollment Approved - Detailed Class Information";
                await _emailService.SendEmailAsync(enrollment.User.Email, subject, scheduleHtml);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send enrollment email");
            }

            TempData["Success"] = $"{enrollment.StudentName} has been successfully enrolled with subject assignments!";
            return RedirectToAction(nameof(ViewStudents));
        }

        // NEW METHOD: Build detailed email HTML
        private string BuildScheduleEmailHtml(Enrollment enrollment,
                                             List<SubjectEnrollment> assignedProfessors,
                                             List<ProfessorSectionAssignment> professorSchedules,
                                             string assignedRoom,
                                             string classSchedule,
                                             string shift)
        {
            var gradeLevel = int.Parse(enrollment.GradeLevel);

            // Build professor details HTML
            string professorsHtml = "";

            if (gradeLevel >= 1 && gradeLevel <= 3)
            {
                var prof = assignedProfessors.FirstOrDefault()?.Professor;
                professorsHtml = $@"
        <div style='background: #f8fafc; padding: 20px; border-radius: 10px; margin: 20px 0;'>
            <h3 style='color: #1f2937; margin: 0 0 15px 0;'>
                <span style='font-size: 1.3em;'>👨‍🏫</span> Class Adviser & Teacher
            </h3>
            <div style='background: white; padding: 15px; border-radius: 8px; border-left: 4px solid #6366f1;'>
                <strong style='color: #1f2937; font-size: 1.1em; display: block;'>{prof?.FullName ?? "To Be Assigned"}</strong>
                <span style='color: #64748b; font-size: 0.95em;'>All Subjects</span>
            </div>
        </div>";
            }
            else if (gradeLevel >= 4 && gradeLevel <= 6)
            {
                professorsHtml = @"
        <div style='background: #f8fafc; padding: 20px; border-radius: 10px; margin: 20px 0;'>
            <h3 style='color: #1f2937; margin: 0 0 15px 0;'>
                <span style='font-size: 1.3em;'></span> Subject Teachers & Schedule
            </h3>
            <div style='display: grid; gap: 12px;'>";

                foreach (var se in assignedProfessors.OrderBy(s => s.Subject))
                {
                    // Try to find schedule information for this professor and subject
                    var schedule = professorSchedules.FirstOrDefault(ps =>
                        ps.ProfessorId == se.ProfessorId &&
                        ps.Subject == se.Subject);

                    string timeSchedule = "Schedule: To Be Announced";
                    if (schedule != null && !string.IsNullOrEmpty(schedule.StartTime) &&
                        !string.IsNullOrEmpty(schedule.EndTime) && !string.IsNullOrEmpty(schedule.DayOfWeek))
                    {
                        timeSchedule = $"<strong style='color: #6366f1;'>{schedule.DayOfWeek}</strong> | {schedule.StartTime} - {schedule.EndTime}";
                    }

                    professorsHtml += $@"
                <div style='background: white; padding: 15px; border-radius: 8px; border-left: 4px solid #6366f1;'>
                    <div style='display: flex; justify-content: space-between; align-items: start; flex-wrap: wrap; gap: 10px;'>
                        <div style='flex: 1; min-width: 200px;'>
                            <strong style='color: #1f2937; font-size: 1.05em; display: block; margin-bottom: 5px;'>
                                📚 {se.Subject}
                            </strong>
                            <span style='color: #64748b; font-size: 0.95em;'>
                                {se.Professor?.FullName ?? "TBA"}
                            </span>
                        </div>
                        <div style='text-align: right; min-width: 200px;'>
                            <span style='color: #475569; font-size: 0.9em; display: block;'>
                                ⏰ {timeSchedule}
                            </span>
                        </div>
                    </div>
                </div>";
                }

                professorsHtml += @"
            </div>
        </div>";
            }

            // Build complete email HTML
            string htmlMessage = $@"
    <html>
    <head>
        <style>
            body {{ font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 0; padding: 0; background-color: #f3f4f6; }}
            .email-container {{ max-width: 650px; margin: 20px auto; background: white; border-radius: 16px; overflow: hidden; box-shadow: 0 10px 30px rgba(0,0,0,0.1); }}
            .header {{ background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); color: white; padding: 40px 30px; text-align: center; }}
            .emoji {{ font-size: 3.5em; margin-bottom: 15px; }}
            .content {{ padding: 35px 30px; }}
            .greeting {{ font-size: 1.15em; color: #374151; margin-bottom: 25px; line-height: 1.6; }}
            .success-message {{ background: linear-gradient(135deg, #d1fae5 0%, #a7f3d0 100%); padding: 25px; border-radius: 12px; margin: 25px 0; border-left: 5px solid #10b981; }}
            .success-message h2 {{ margin: 0 0 10px 0; color: #065f46; font-size: 1.4em; }}
            .details-section {{ background: #f9fafb; padding: 25px; border-radius: 12px; margin: 25px 0; }}
            .details-section h3 {{ margin: 0 0 20px 0; color: #1f2937; font-size: 1.3em; border-bottom: 2px solid #e5e7eb; padding-bottom: 10px; }}
            .detail-grid {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(250px, 1fr)); gap: 15px; }}
            .detail-item {{ background: white; padding: 15px; border-radius: 8px; border: 1px solid #e5e7eb; }}
            .detail-item strong {{ display: block; color: #6b7280; font-size: 0.85em; text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 6px; }}
            .detail-item span {{ display: block; color: #1f2937; font-size: 1.05em; font-weight: 600; }}
            .schedule-box {{ background: linear-gradient(135deg, #6366f1 0%, #8b5cf6 100%); color: white; padding: 25px; border-radius: 12px; margin: 20px 0; text-align: center; }}
            .schedule-box h3 {{ margin: 0 0 15px 0; font-size: 1.3em; }}
            .time {{ font-size: 2em; font-weight: 700; margin: 15px 0; }}
            .shift {{ display: inline-block; padding: 8px 20px; background: rgba(255,255,255,0.2); border-radius: 20px; margin-top: 10px; font-weight: 600; }}
            .important-info {{ background: #fef3c7; padding: 20px; border-radius: 10px; border-left: 4px solid #f59e0b; margin: 25px 0; }}
            .important-info h3 {{ margin: 0 0 15px 0; color: #92400e; }}
            .important-info ul {{ margin: 10px 0 0 20px; color: #92400e; line-height: 1.8; }}
            .contact-section {{ background: #dbeafe; padding: 20px; border-radius: 10px; margin: 25px 0; }}
            .contact-section h3 {{ margin: 0 0 15px 0; color: #1e40af; }}
            .contact-info {{ color: #1e40af; line-height: 1.8; }}
            .footer {{ background: #1f2937; color: #9ca3af; text-align: center; padding: 25px; font-size: 0.9em; }}
        </style>
    </head>
    <body>
        <div class='email-container'>
            <div class='header'>
                <div class='emoji'>🎓</div>
                <h1 style='margin: 0 0 10px 0; font-size: 2em;'>Enrollment Approved!</h1>
                <p style='margin: 0; opacity: 0.95; font-size: 1.1em;'>Fort Bonifacio Elementary School</p>
            </div>
            
            <div class='content'>
                <div class='greeting'>
                    Dear <strong>{enrollment.ParentName}</strong>,
                </div>
                
                <div class='success-message'>
                    <h2>✅ Congratulations!</h2>
                    <p style='margin: 10px 0 0; color: #065f46; line-height: 1.6;'>
                        We are pleased to inform you that the enrollment application for <strong>{enrollment.StudentName}</strong> has been successfully approved and enrolled!
                    </p>
                </div>

                <div class='details-section'>
                    <h3> Student & Class Information</h3>
                    
                    <div class='detail-grid'>
                        <div class='detail-item'>
                            <strong> Student Name</strong>
                            <span>{enrollment.StudentName}</span>
                        </div>
                        <div class='detail-item'>
                            <strong> Grade Level</strong>
                            <span>Grade {enrollment.GradeLevel}</span>
                        </div>
                        <div class='detail-item'>
                            <strong> Section</strong>
                            <span>Section {enrollment.Section}</span>
                        </div>
                        <div class='detail-item'>
                            <strong> Assigned Room</strong>
                            <span>{assignedRoom}</span>
                        </div>
                    </div>
                </div>

                {professorsHtml}

                <div class='schedule-box'>
                    <h3> Class Schedule</h3>
                    <div class='time'>{classSchedule}</div>
                    <div class='shift'>{shift} Shift</div>
                    <p style='margin: 15px 0 0; opacity: 0.95; font-size: 1em;'>
                        📅 Monday to Friday | ⏱️ 5 Hours Daily
                    </p>
                </div>

                <div class='important-info'>
                    <h3>⚠️ Important Reminders</h3>
                    <ul>
                        <li><strong>First Day of Class:</strong> Please check the school calendar for the start date.</li>
                        <li><strong>Required Items:</strong> School uniform, supplies, and ID requirements will be sent separately.</li>
                        <li><strong>Orientation:</strong> Watch for updates about the parent-student orientation schedule.</li>
                        <li><strong>Class Schedule:</strong> Please note your subject schedules and teacher assignments above.</li>
                        <li><strong>Punctuality:</strong> Students should arrive at least 15 minutes before class starts.</li>
                    </ul>
                </div>

                <div class='contact-section'>
                    <h3>📞 Need Help?</h3>
                    <div class='contact-info'>
                        <p style='margin: 0 0 10px 0;'>If you have any questions or concerns about the schedule, teachers, or any other matters, please don't hesitate to contact us:</p>
                        <p style='margin: 8px 0;'><strong>📞 Phone:</strong> (02) 239 8307</p>
                        <p style='margin: 8px 0;'><strong>✉️ Email:</strong> fortbonifacio01@gmail.com</p>
                        <p style='margin: 8px 0;'><strong>🏫 Address:</strong> Fort Bonifacio, Taguig City</p>
                    </div>
                </div>
            </div>
            
            <div class='footer'>
                <p style='margin: 0 0 5px 0;'>&copy; {DateTime.Now.Year} Elementary School. All Rights Reserved.</p>
                <p style='margin: 5px 0 0 0;'>This is an automated email. Please do not reply directly to this message.</p>
            </div>
        </div>
    </body>
    </html>";

            return htmlMessage;
        }

        // Decline enrollment
        [HttpPost]
        public async Task<IActionResult> DeclineEnrollment(int id)
        {
            var enrollment = await _context.Enrollments
                .Include(e => e.User)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (enrollment == null)
            {
                return NotFound();
            }

            enrollment.Status = "rejected";
            await _context.SaveChangesAsync();

            // Send rejection email
            try
            {
                string subject = "Enrollment Application Update - Elementary School";
                string htmlMessage = $@"
                    <html>
                    <body style='font-family: Arial, sans-serif; padding: 20px;'>
                        <div style='max-width: 600px; margin: 0 auto; background: white; border-radius: 10px; padding: 30px;'>
                            <h2 style='color: #dc3545; text-align: center;'>Enrollment Application Update</h2>
                            
                            <p>Dear Parent/Guardian,</p>
                            
                            <p>We regret to inform you that the enrollment application for <strong>{enrollment.StudentName}</strong> could not be approved at this time.</p>
                            
                            <p>For more information or to resubmit your application, please contact our enrollment office.</p>
                            
                            <p style='margin-top: 30px; color: #666;'>
                                Contact us at:<br>
                                📞 (02) 239 8307<br>
                                ✉️ fortbonifacio01@gmail.com
                            </p>
                        </div>
                    </body>
                    </html>";

                await _emailService.SendEmailAsync(enrollment.User.Email, subject, htmlMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send rejection email");
            }

            TempData["Info"] = $"Enrollment for {enrollment.StudentName} has been declined.";
            return RedirectToAction(nameof(ViewStudents));
        }

        // Professor Management
        public async Task<IActionResult> ManageProfessors()
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var professors = await _context.Users
                .Where(u => u.Role == "professor")
                .OrderBy(u => u.AssignedGradeLevel)
                .ThenBy(u => u.AssignedSection)
                .ToListAsync();

            // Get section assignment counts for each professor
            var professorIds = professors.Select(p => p.Id).ToList();
            var assignmentCounts = await _context.ProfessorSectionAssignments
                .Where(a => professorIds.Contains(a.ProfessorId))
                .GroupBy(a => a.ProfessorId)
                .Select(g => new { ProfessorId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ProfessorId, x => x.Count);

            ViewBag.AssignmentCounts = assignmentCounts;

            return View(professors);
        }

        // Add Professor - GET
        public IActionResult AddProfessor()
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            return View();
        }

        // Add Professor - POST
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddProfessor(AddProfessorViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // Prevent duplicate accounts
            var existingUser = await _userManager.FindByEmailAsync(model.Email);
            if (existingUser != null)
            {
                ModelState.AddModelError("Email", "An account with this email already exists.");
                return View(model);
            }

            // Validate grade level assignment
            if (string.IsNullOrEmpty(model.GradeLevel))
            {
                ModelState.AddModelError("GradeLevel", "Grade level is required.");
                return View(model);
            }

            int gradeLevel = int.Parse(model.GradeLevel);

            // Grades 1-3: Only one professor per grade (no section, no subject)
            if (gradeLevel >= 1 && gradeLevel <= 3)
            {
                // Check if grade already has a professor (in Users table)
                var existingProfessor = await _context.Users
                    .FirstOrDefaultAsync(u => u.Role == "professor" &&
                                             u.AssignedGradeLevel == model.GradeLevel);

                if (existingProfessor != null)
                {
                    ModelState.AddModelError("GradeLevel",
                        $"Grade {model.GradeLevel} already has an assigned professor: {existingProfessor.FullName}. Only ONE professor is allowed for grades 1-3 (they handle all sections and subjects).");
                    return View(model);
                }

                // Also check ProfessorSectionAssignments table
                var existingAssignment = await _context.ProfessorSectionAssignments
                    .FirstOrDefaultAsync(a => a.GradeLevel == model.GradeLevel);

                if (existingAssignment != null)
                {
                    var assignedProf = await _context.Users.FindAsync(existingAssignment.ProfessorId);
                    ModelState.AddModelError("GradeLevel",
                        $"Grade {model.GradeLevel} already has an assigned professor: {assignedProf?.FullName}. Only ONE professor is allowed for grades 1-3.");
                    return View(model);
                }
            }

            var user = new Users
            {
                FullName = model.FullName,
                UserName = model.Email,
                Email = model.Email,
                EmailConfirmed = true,
                Role = "professor",
                AssignedGradeLevel = model.GradeLevel,
                AssignedSection = null, // Will be assigned later for grades 4-6
                AssignedSubject = null,  // Will be assigned later for grades 4-6
                AssignedRoom = null
            };

            var result = await _userManager.CreateAsync(user, model.Password);

            if (result.Succeeded)
            {
                string message = gradeLevel >= 1 && gradeLevel <= 3
                    ? $"Professor {model.FullName} has been added successfully for Grade {model.GradeLevel}. They handle all sections and subjects for this grade."
                    : $"Professor {model.FullName} has been added successfully for Grade {model.GradeLevel}. You can now add section assignments from Manage Professors.";

                TempData["Success"] = message;
                return RedirectToAction(nameof(ManageProfessors));
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        // Edit Professor - GET
        public async Task<IActionResult> EditProfessor(string id)
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var professor = await _userManager.FindByIdAsync(id);
            if (professor == null || professor.Role != "professor")
            {
                return NotFound();
            }

            var model = new EditProfessorViewModel
            {
                Id = professor.Id,
                FullName = professor.FullName,
                Email = professor.Email,
                GradeLevel = professor.AssignedGradeLevel,
                Section = professor.AssignedSection,
                Subject = professor.AssignedSubject
            };

            return View(model);
        }

        // Edit Professor - POST
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditProfessor(EditProfessorViewModel model)
        {
            if (ModelState.IsValid)
            {
                var professor = await _userManager.FindByIdAsync(model.Id);
                if (professor == null)
                {
                    return NotFound();
                }

                int gradeLevel = int.Parse(model.GradeLevel);

                // Grades 1-3: Only one professor per grade (no section, no subject)
                if (gradeLevel >= 1 && gradeLevel <= 3)
                {
                    // Check if grade already has another professor (in Users table)
                    var existingProfessor = await _context.Users
                        .FirstOrDefaultAsync(u => u.Role == "professor" &&
                                                 u.AssignedGradeLevel == model.GradeLevel &&
                                                 u.Id != model.Id);

                    if (existingProfessor != null)
                    {
                        ModelState.AddModelError("GradeLevel",
                            $"Grade {model.GradeLevel} already has an assigned professor: {existingProfessor.FullName}. Only ONE professor is allowed for grades 1-3 (they handle all sections and subjects).");
                        return View(model);
                    }

                    // Check ProfessorSectionAssignments table
                    var existingAssignment = await _context.ProfessorSectionAssignments
                        .FirstOrDefaultAsync(a => a.GradeLevel == model.GradeLevel &&
                                                 a.ProfessorId != model.Id);

                    if (existingAssignment != null)
                    {
                        var assignedProf = await _context.Users.FindAsync(existingAssignment.ProfessorId);
                        ModelState.AddModelError("GradeLevel",
                            $"Grade {model.GradeLevel} already has an assigned professor: {assignedProf?.FullName}. Only ONE professor is allowed for grades 1-3.");
                        return View(model);
                    }

                    // For grades 1-3, section and subject are not needed
                    model.Section = null;
                    model.Subject = null;
                }
                // Grades 4-6: Up to 6 professors per section, each with different subjects
                // Grades 4-6: Up to 6 professors per section, each with different subjects
                else if (gradeLevel >= 4 && gradeLevel <= 6)
                {
                    // Validate section is provided
                    if (!model.Section.HasValue)
                    {
                        ModelState.AddModelError("Section", "Section is required for grades 4-6.");
                        return View(model);
                    }

                    // Validate subject is provided
                    if (string.IsNullOrEmpty(model.Subject))
                    {
                        ModelState.AddModelError("Subject", "Subject is required for grades 4-6.");
                        return View(model);
                    }

                    // NEW: Check if professor is trying to change to a different subject
                    var currentSubject = professor.AssignedSubject;
                    var allProfessorAssignments = await _context.ProfessorSectionAssignments
                        .Where(a => a.ProfessorId == model.Id)
                        .Select(a => a.Subject)
                        .ToListAsync();

                    // If professor has existing assignments, check if they're trying to change subject
                    if (!string.IsNullOrEmpty(currentSubject) && currentSubject != model.Subject)
                    {
                        ModelState.AddModelError("Subject",
                            $"This professor is already assigned to teach {currentSubject}. A professor can only teach ONE subject. If you want to change their subject, you must first remove all their current assignments.");
                        return View(model);
                    }

                    if (allProfessorAssignments.Any() && allProfessorAssignments.Any(s => s != model.Subject))
                    {
                        var existingSubject = allProfessorAssignments.First(s => s != model.Subject);
                        ModelState.AddModelError("Subject",
                            $"This professor is already assigned to teach {existingSubject} in other sections. A professor can only teach ONE subject. If you want to change their subject, you must first remove all their current assignments.");
                        return View(model);
                    }

                    // Check if this section already has another professor for this subject
                    var existingSubjectProfessor = await _context.Users
                        .FirstOrDefaultAsync(u => u.Role == "professor" &&
                                                 u.AssignedGradeLevel == model.GradeLevel &&
                                                 u.AssignedSection == model.Section &&
                                                 u.AssignedSubject == model.Subject &&
                                                 u.Id != model.Id);

                    if (existingSubjectProfessor != null)
                    {
                        ModelState.AddModelError("Subject",
                            $"Grade {model.GradeLevel} - Section {model.Section} already has a professor for {model.Subject}: {existingSubjectProfessor.FullName}");
                        return View(model);
                    }

                    // Check how many professors are already assigned to this section (excluding current professor)
                    var professorsInSection = await _context.Users
                        .CountAsync(u => u.Role == "professor" &&
                                        u.AssignedGradeLevel == model.GradeLevel &&
                                        u.AssignedSection == model.Section &&
                                        u.Id != model.Id);

                    // If changing section, check if new section has space
                    if (professor.AssignedSection != model.Section && professorsInSection >= 6)
                    {
                        ModelState.AddModelError("Section",
                            $"Grade {model.GradeLevel} - Section {model.Section} already has 6 professors (maximum allowed).");
                        return View(model);
                    }
                }

                // Update room assignment
                string assignedRoom = model.Section.HasValue
                    ? RoomAssignmentHelper.GetRoomForSection(model.GradeLevel, model.Section.Value)
                    : null;

                professor.FullName = model.FullName;
                professor.Email = model.Email;
                professor.UserName = model.Email;
                professor.AssignedGradeLevel = model.GradeLevel;
                professor.AssignedSection = model.Section;
                professor.AssignedSubject = model.Subject;
                professor.AssignedRoom = assignedRoom;

                var result = await _userManager.UpdateAsync(professor);

                if (result.Succeeded)
                {
                    TempData["Success"] = $"Professor {model.FullName} has been updated successfully.";
                    return RedirectToAction(nameof(ManageProfessors));
                }

                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }
            }

            return View(model);
        }

        // Delete Professor
        [HttpPost]
        public async Task<IActionResult> DeleteProfessor(string id)
        {
            var professor = await _userManager.FindByIdAsync(id);
            if (professor == null || professor.Role != "professor")
            {
                return NotFound();
            }

            // Remove dependent data that references this professor to avoid FK violations
            var subjectEnrollments = await _context.SubjectEnrollments
                .Where(se => se.ProfessorId == id)
                .ToListAsync();
            if (subjectEnrollments.Count > 0)
            {
                _context.SubjectEnrollments.RemoveRange(subjectEnrollments);
            }

            var sectionAssignments = await _context.ProfessorSectionAssignments
                .Where(a => a.ProfessorId == id)
                .ToListAsync();
            if (sectionAssignments.Count > 0)
            {
                _context.ProfessorSectionAssignments.RemoveRange(sectionAssignments);
            }

            if (subjectEnrollments.Count > 0 || sectionAssignments.Count > 0)
            {
                await _context.SaveChangesAsync();
            }

            var result = await _userManager.DeleteAsync(professor);

            if (result.Succeeded)
            {
                TempData["Success"] = $"Professor {professor.FullName} has been deleted successfully.";
            }
            else
            {
                TempData["Error"] = "Failed to delete professor.";
            }

            return RedirectToAction(nameof(ManageProfessors));
        }

        // Manage Professor Sections - View all sections assigned to a professor
        public async Task<IActionResult> ManageProfessorSections(string id)
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var professor = await _userManager.FindByIdAsync(id);
            if (professor == null || professor.Role != "professor")
            {
                return NotFound();
            }

            // Get all section assignments for this professor
            var assignments = await _context.ProfessorSectionAssignments
                .Where(a => a.ProfessorId == id)
                .OrderBy(a => a.GradeLevel)
                .ThenBy(a => a.Section)
                .ToListAsync();

            // Also include the primary assignment from the Users table if it exists
            if (professor.AssignedGradeLevel != null && professor.AssignedSection.HasValue)
            {
                var primaryExists = assignments.Any(a =>
                    a.GradeLevel == professor.AssignedGradeLevel &&
                    a.Section == professor.AssignedSection.Value);

                if (!primaryExists)
                {
                    assignments.Insert(0, new ProfessorSectionAssignment
                    {
                        Id = 0, // Temporary ID
                        ProfessorId = professor.Id,
                        GradeLevel = professor.AssignedGradeLevel,
                        Section = professor.AssignedSection.Value,
                        Subject = professor.AssignedSubject,
                        AssignedRoom = professor.AssignedRoom
                    });
                }
            }

            ViewBag.Professor = professor;
            ViewBag.Assignments = assignments;
            ViewBag.GradeLevel = professor.AssignedGradeLevel;

            return View();
        }

        private async Task<bool> HasTimeConflict(string gradeLevel, int section, string startTime, string endTime, string dayOfWeek, string professorId = null)
        {
            if (string.IsNullOrEmpty(startTime) || string.IsNullOrEmpty(endTime) || string.IsNullOrEmpty(dayOfWeek))
            {
                return false; // No time specified, no conflict checking needed
            }

            // Get all assignments for this grade and section
            var existingAssignments = await _context.ProfessorSectionAssignments
                .Where(a => a.GradeLevel == gradeLevel &&
                           a.Section == section &&
                           a.StartTime != null &&
                           a.EndTime != null &&
                           (professorId == null || a.ProfessorId != professorId))
                .ToListAsync();

            var newStart = TimeSpan.Parse(startTime);
            var newEnd = TimeSpan.Parse(endTime);

            foreach (var assignment in existingAssignments)
            {
                // Check if days overlap
                if (!string.IsNullOrEmpty(assignment.DayOfWeek))
                {
                    var existingDays = assignment.DayOfWeek.Split(',').Select(d => d.Trim()).ToList();
                    var newDays = dayOfWeek.Split(',').Select(d => d.Trim()).ToList();

                    bool daysOverlap = existingDays.Any(d => newDays.Contains(d));

                    if (daysOverlap)
                    {
                        var existingStart = TimeSpan.Parse(assignment.StartTime);
                        var existingEnd = TimeSpan.Parse(assignment.EndTime);

                        // Check if times overlap
                        bool timesOverlap = (newStart < existingEnd && newEnd > existingStart);

                        if (timesOverlap)
                        {
                            return true; // Conflict found
                        }
                    }
                }
            }

            return false; // No conflict
        }

        // Check if professor has a time conflict with their own existing assignments
        private async Task<bool> HasProfessorTimeConflict(string professorId, string startTime, string endTime, string dayOfWeek, int? excludeAssignmentId = null)
        {
            if (string.IsNullOrEmpty(startTime) || string.IsNullOrEmpty(endTime) || string.IsNullOrEmpty(dayOfWeek))
            {
                return false; // No time specified, no conflict checking needed
            }

            // Get all existing assignments for this professor
            var professorAssignments = await _context.ProfessorSectionAssignments
                .Where(a => a.ProfessorId == professorId &&
                           a.StartTime != null &&
                           a.EndTime != null &&
                           (excludeAssignmentId == null || a.Id != excludeAssignmentId))
                .ToListAsync();

            var newStart = TimeSpan.Parse(startTime);
            var newEnd = TimeSpan.Parse(endTime);

            foreach (var assignment in professorAssignments)
            {
                // Check if days overlap
                if (!string.IsNullOrEmpty(assignment.DayOfWeek))
                {
                    var existingDays = assignment.DayOfWeek.Split(',').Select(d => d.Trim()).ToList();
                    var newDays = dayOfWeek.Split(',').Select(d => d.Trim()).ToList();

                    bool daysOverlap = existingDays.Any(d => newDays.Contains(d));

                    if (daysOverlap)
                    {
                        var existingStart = TimeSpan.Parse(assignment.StartTime);
                        var existingEnd = TimeSpan.Parse(assignment.EndTime);

                        // Check if times overlap
                        bool timesOverlap = (newStart < existingEnd && newEnd > existingStart);

                        if (timesOverlap)
                        {
                            return true; // Conflict found - professor already has an assignment at this time
                        }
                    }
                }
            }

            return false; // No conflict
        }

        // Add Section to Professor
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddSectionToProfessor(
    string professorId,
    string gradeLevel,
    int section,
    string? subject,
    string? startTime,
    string? endTime,
    string? dayOfWeek)
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var professor = await _userManager.FindByIdAsync(professorId);
            if (professor == null || professor.Role != "professor")
            {
                return NotFound();
            }

            int grade = int.Parse(gradeLevel);

            if (grade >= 1 && grade <= 3)
            {
                TempData["Error"] = $"Cannot add additional sections for Grade {gradeLevel}. Grades 1-3 can only have ONE professor who handles ALL sections and subjects for that grade.";
                return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
            }

            // Validate time fields - if any time field is filled, all must be filled
            bool hasAnyTimeField = !string.IsNullOrEmpty(startTime) || !string.IsNullOrEmpty(endTime) || !string.IsNullOrEmpty(dayOfWeek);
            bool hasAllTimeFields = !string.IsNullOrEmpty(startTime) && !string.IsNullOrEmpty(endTime) && !string.IsNullOrEmpty(dayOfWeek);

            if (hasAnyTimeField && !hasAllTimeFields)
            {
                TempData["Error"] = "If you specify a schedule, you must fill in all time fields (Day, Start Time, and End Time).";
                return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
            }

            // Validate that end time is after start time
            if (!string.IsNullOrEmpty(startTime) && !string.IsNullOrEmpty(endTime))
            {
                try
                {
                    var start = TimeSpan.Parse(startTime);
                    var end = TimeSpan.Parse(endTime);

                    if (start >= end)
                    {
                        TempData["Error"] = "End time must be after start time.";
                        return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
                    }
                }
                catch
                {
                    TempData["Error"] = "Invalid time format. Please use HH:mm format (e.g., 08:00).";
                    return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
                }
            }

            // Check for time conflicts
            if (hasAllTimeFields)
            {
                // Check if another professor has a conflict in this section
                bool hasConflict = await HasTimeConflict(gradeLevel, section, startTime, endTime, dayOfWeek, professorId);
                if (hasConflict)
                {
                    TempData["Error"] = $"Time conflict detected! Another professor already has a class scheduled for Grade {gradeLevel} - Section {section} during this time period ({dayOfWeek}: {startTime} - {endTime}).";
                    return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
                }

                // Check if this professor already has an assignment at the same time (different section)
                bool hasProfessorConflict = await HasProfessorTimeConflict(professorId, startTime, endTime, dayOfWeek);
                if (hasProfessorConflict)
                {
                    // Get the conflicting assignment details for better error message
                    var conflictingAssignment = await _context.ProfessorSectionAssignments
                        .Where(a => a.ProfessorId == professorId &&
                                   a.StartTime != null &&
                                   a.EndTime != null &&
                                   a.DayOfWeek != null)
                        .ToListAsync();

                    var conflict = conflictingAssignment.FirstOrDefault(a =>
                    {
                        if (string.IsNullOrEmpty(a.DayOfWeek)) return false;
                        var existingDays = a.DayOfWeek.Split(',').Select(d => d.Trim()).ToList();
                        var newDays = dayOfWeek.Split(',').Select(d => d.Trim()).ToList();
                        bool daysOverlap = existingDays.Any(d => newDays.Contains(d));

                        if (daysOverlap)
                        {
                            var existingStart = TimeSpan.Parse(a.StartTime);
                            var existingEnd = TimeSpan.Parse(a.EndTime);
                            var newStart = TimeSpan.Parse(startTime);
                            var newEnd = TimeSpan.Parse(endTime);
                            bool timesOverlap = (newStart < existingEnd && newEnd > existingStart);
                            return timesOverlap;
                        }
                        return false;
                    });

                    if (conflict != null)
                    {
                        TempData["Error"] = $"⚠️ Time conflict detected! This professor is already assigned to Grade {conflict.GradeLevel} - Section {conflict.Section} at the same time ({conflict.DayOfWeek}: {conflict.StartTime} - {conflict.EndTime}). A professor cannot be assigned to different sections at the same time.";
                        return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
                    }
                    else
                    {
                        TempData["Error"] = $"⚠️ Time conflict detected! This professor already has an assignment at this time ({dayOfWeek}: {startTime} - {endTime}). A professor cannot be assigned to different sections at the same time.";
                        return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
                    }
                }
            }

            // Check if assignment already exists in ProfessorSectionAssignments
            var existing = await _context.ProfessorSectionAssignments
                .FirstOrDefaultAsync(a => a.ProfessorId == professorId &&
                                         a.GradeLevel == gradeLevel &&
                                         a.Section == section);

            if (existing != null)
            {
                TempData["Error"] = $"Professor is already assigned to Grade {gradeLevel} - Section {section}.";
                return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
            }

            // Check if this is the professor's primary assignment
            if (professor.AssignedGradeLevel == gradeLevel && professor.AssignedSection == section)
            {
                TempData["Error"] = $"This is already the professor's primary assignment. Use Edit to change it.";
                return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
            }

            // For grades 4-6, check subject conflicts
            // For grades 4-6, check subject conflicts
            if (grade >= 4 && grade <= 6)
            {
                if (string.IsNullOrEmpty(subject))
                {
                    TempData["Error"] = "Subject is required for grades 4-6.";
                    return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
                }

                // NEW: Check if professor is trying to teach a different subject
                var professorCurrentSubject = professor.AssignedSubject;
                var allProfessorAssignments = await _context.ProfessorSectionAssignments
                    .Where(a => a.ProfessorId == professorId)
                    .Select(a => a.Subject)
                    .ToListAsync();

                // If professor has a primary subject, new assignment must be the same subject
                if (!string.IsNullOrEmpty(professorCurrentSubject) && professorCurrentSubject != subject)
                {
                    TempData["Error"] = $"This professor is already assigned to teach {professorCurrentSubject}. A professor can only teach ONE subject across all grades and sections.";
                    return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
                }

                // If professor has other assignments, check they're all the same subject
                if (allProfessorAssignments.Any() && allProfessorAssignments.Any(s => s != subject))
                {
                    var existingSubject = allProfessorAssignments.First(s => s != subject);
                    TempData["Error"] = $"This professor is already assigned to teach {existingSubject} in other sections. A professor can only teach ONE subject across all grades and sections.";
                    return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
                }

                // Check if another professor already teaches this subject in this section (in assignments table)
                // Check if another professor already teaches this subject in this section (in assignments table)
                var subjectConflictInAssignments = await _context.ProfessorSectionAssignments
                    .AnyAsync(a => a.GradeLevel == gradeLevel &&
                                 a.Section == section &&
                                 a.Subject == subject &&
                                 a.ProfessorId != professorId);

                // Also check in Users table (primary assignments)
                var subjectConflictInUsers = await _context.Users
                    .AnyAsync(u => u.Role == "professor" &&
                                 u.AssignedGradeLevel == gradeLevel &&
                                 u.AssignedSection == section &&
                                 u.AssignedSubject == subject &&
                                 u.Id != professorId);

                if (subjectConflictInAssignments || subjectConflictInUsers)
                {
                    TempData["Error"] = $"Another professor is already assigned to Grade {gradeLevel} - Section {section} for {subject}.";
                    return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
                }
            }

            // Get room assignment
            string assignedRoom = RoomAssignmentHelper.GetRoomForSection(gradeLevel, section);

            var assignment = new ProfessorSectionAssignment
            {
                ProfessorId = professorId,
                GradeLevel = gradeLevel,
                Section = section,
                Subject = subject,
                AssignedRoom = assignedRoom,
                StartTime = startTime,
                EndTime = endTime,
                DayOfWeek = dayOfWeek
            };

            _context.ProfessorSectionAssignments.Add(assignment);
            await _context.SaveChangesAsync();

            string scheduleInfo = hasAllTimeFields
                ? $" ({dayOfWeek}: {startTime} - {endTime})"
                : "";

            TempData["Success"] = $"Professor has been assigned to Grade {gradeLevel} - Section {section}{scheduleInfo}.";
            return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
        }

        // Remove Section from Professor
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveSectionFromProfessor(int assignmentId, string professorId)
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var assignment = await _context.ProfessorSectionAssignments.FindAsync(assignmentId);
            if (assignment == null || assignment.ProfessorId != professorId)
            {
                return NotFound();
            }

            _context.ProfessorSectionAssignments.Remove(assignment);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Section assignment removed successfully.";
            return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
        }

        // Assign Grade Level to Professor
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignGradeLevelToProfessor(string professorId, string gradeLevel)
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var professor = await _userManager.FindByIdAsync(professorId);
            if (professor == null || professor.Role != "professor")
            {
                return NotFound();
            }

            if (string.IsNullOrEmpty(gradeLevel))
            {
                TempData["Error"] = "Grade level is required.";
                return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
            }

            int grade = int.Parse(gradeLevel);

            // Validation for grades 1-3: Only one professor per grade
            if (grade >= 1 && grade <= 3)
            {
                // Check if grade already has another professor (in Users table)
                var existingProfessor = await _context.Users
                    .FirstOrDefaultAsync(u => u.Role == "professor" &&
                                             u.AssignedGradeLevel == gradeLevel &&
                                             u.Id != professorId);

                if (existingProfessor != null)
                {
                    TempData["Error"] = $"Grade {gradeLevel} already has an assigned professor: {existingProfessor.FullName}. Only ONE professor is allowed for grades 1-3 (they handle all sections and subjects).";
                    return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
                }

                // Check ProfessorSectionAssignments table
                var existingAssignment = await _context.ProfessorSectionAssignments
                    .FirstOrDefaultAsync(a => a.GradeLevel == gradeLevel &&
                                             a.ProfessorId != professorId);

                if (existingAssignment != null)
                {
                    var assignedProf = await _context.Users.FindAsync(existingAssignment.ProfessorId);
                    TempData["Error"] = $"Grade {gradeLevel} already has an assigned professor: {assignedProf?.FullName}. Only ONE professor is allowed for grades 1-3.";
                    return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
                }

                // For grades 1-3, clear section and subject (they handle all)
                professor.AssignedGradeLevel = gradeLevel;
                professor.AssignedSection = null;
                professor.AssignedSubject = null;
                professor.AssignedRoom = null;
            }
            else if (grade >= 4 && grade <= 6)
            {
                // For grades 4-6, just set the grade level
                // Section and subject will be assigned separately
                professor.AssignedGradeLevel = gradeLevel;
                // Don't clear section/subject if they already have assignments
            }
            else
            {
                TempData["Error"] = "Invalid grade level. Please select a grade from 1-6.";
                return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
            }

            var result = await _userManager.UpdateAsync(professor);

            if (result.Succeeded)
            {
                string message = grade >= 1 && grade <= 3
                    ? $"Professor has been assigned to Grade {gradeLevel}. They will handle all sections and subjects for this grade."
                    : $"Professor has been assigned to Grade {gradeLevel}. You can now add section assignments.";

                TempData["Success"] = message;
            }
            else
            {
                TempData["Error"] = "Failed to assign grade level. Please try again.";
            }

            return RedirectToAction(nameof(ManageProfessorSections), new { id = professorId });
        }

        // Section Management
        public async Task<IActionResult> ManageSections()
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var viewModel = new SectionManagementViewModel();

            // Get all grade levels (1-6)
            for (int grade = 1; grade <= 6; grade++)
            {
                var gradeLevel = new GradeLevelSections
                {
                    GradeLevel = grade.ToString()
                };

                // Get students grouped by section for this grade
                var studentsInGrade = await _context.Enrollments
                    .Where(e => e.GradeLevel == grade.ToString() && e.Status == "approved")
                    .GroupBy(e => e.Section)
                    .Select(g => new SectionInfo
                    {
                        SectionNumber = g.Key ?? 0,
                        StudentCount = g.Count()
                    })
                    .ToListAsync();

                gradeLevel.Sections = studentsInGrade;
                gradeLevel.TotalStudents = studentsInGrade.Sum(s => s.StudentCount);

                viewModel.GradeLevels.Add(gradeLevel);
            }

            return View(viewModel);
        }

        // View Students in Specific Section
        public async Task<IActionResult> ViewSectionStudents(string gradeLevel, int section)
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var students = await _context.Enrollments
                .Include(e => e.User)
                .Where(e => e.GradeLevel == gradeLevel && e.Section == section && e.Status == "approved")
                .OrderBy(e => e.StudentName)
                .ToListAsync();

            ViewBag.GradeLevel = gradeLevel;
            ViewBag.Section = section;
            ViewBag.StudentCount = students.Count;

            return View(students);
        }

        // Class Schedules
        public async Task<IActionResult> ClassSchedules()
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var schedules = new List<ClassScheduleViewModel>();

            for (int grade = 1; grade <= 6; grade++)
            {
                var gradeLevel = grade.ToString();
                var gradeInt = grade;
                bool isMorningShift = gradeInt % 2 == 0; // Grades 2, 4, 6 = Morning
                string shift = isMorningShift ? "Morning" : "Afternoon";
                string schedule = isMorningShift ? "7:00 AM - 12:00 PM" : "1:00 PM - 6:00 PM";
                string startTime = isMorningShift ? "7:00 AM" : "1:00 PM";
                string endTime = isMorningShift ? "12:00 PM" : "6:00 PM";

                var scheduleViewModel = new ClassScheduleViewModel
                {
                    GradeLevel = gradeLevel,
                    Schedule = schedule,
                    StartTime = startTime,
                    EndTime = endTime,
                    Shift = shift,
                    Professors = new List<ProfessorScheduleInfo>()
                };

                // Get professors for this grade level
                var professors = await _context.Users
                    .Where(u => u.Role == "professor" && u.AssignedGradeLevel == gradeLevel)
                    .OrderBy(u => u.AssignedSection)
                    .ThenBy(u => u.AssignedSubject)
                    .ToListAsync();

                foreach (var professor in professors)
                {
                    scheduleViewModel.Professors.Add(new ProfessorScheduleInfo
                    {
                        ProfessorName = professor.FullName,
                        Subject = professor.AssignedSubject ?? "All Subjects",
                        Section = professor.AssignedSection,
                        Room = professor.AssignedRoom ?? "TBA",
                        TimeSlot = schedule
                    });
                }

                schedules.Add(scheduleViewModel);
            }

            return View(schedules);
        }

        // Remove Student from Section (delete enrollment so they can reapply)
        [HttpPost]
        public async Task<IActionResult> RemoveStudent(int id)
        {
            var enrollment = await _context.Enrollments
                .Include(e => e.User)
                .FirstOrDefaultAsync(e => e.Id == id);
            if (enrollment == null)
            {
                TempData["Error"] = "Student enrollment not found.";
                return RedirectToAction(nameof(ViewStudents));
            }

            var gradeLevel = enrollment.GradeLevel;
            var section = enrollment.Section;
            var studentName = enrollment.StudentName;

            // Remove related subject enrollments before deleting enrollment
            var subjectEnrollments = await _context.SubjectEnrollments
                .Where(se => se.EnrollmentId == enrollment.Id)
                .ToListAsync();
            if (subjectEnrollments.Count > 0)
            {
                _context.SubjectEnrollments.RemoveRange(subjectEnrollments);
            }

            _context.Enrollments.Remove(enrollment);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Student {studentName} has been removed from Grade {gradeLevel} - Section {section}. Their enrollment record was deleted and they can submit a new application.";

            if (gradeLevel != null && section.HasValue)
            {
                return RedirectToAction(nameof(ViewSectionStudents), new { gradeLevel, section });
            }

            return RedirectToAction(nameof(ViewStudents));
        }

        // Reassign Student to Section - GET
        public async Task<IActionResult> ReassignStudent(int id)
        {
            var role = HttpContext.Session.GetString("Role");
            if (role != "admin")
            {
                return RedirectToAction("Index", "Home");
            }

            var enrollment = await _context.Enrollments
                .Include(e => e.User)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (enrollment == null)
            {
                TempData["Error"] = "Student enrollment not found.";
                return RedirectToAction(nameof(ViewStudents));
            }


            var gradeLevel = enrollment.GradeLevel;
            var sectionsWithCapacity = new List<int>();
            var sectionCapacityData = new Dictionary<int, int>();


            for (int i = 1; i <= 8; i++)
            {
                var studentsInSection = await _context.Enrollments
                    .CountAsync(e => e.GradeLevel == gradeLevel &&
                                   e.Section == i &&
                                   e.Status == "approved");

                sectionCapacityData[i] = studentsInSection;

                if (studentsInSection < 40)
                {
                    sectionsWithCapacity.Add(i);
                }
            }

            ViewBag.AvailableSections = sectionsWithCapacity;
            ViewBag.SectionCapacity = sectionCapacityData;
            ViewBag.GradeLevel = gradeLevel;

            return View(enrollment);
        }

        [HttpPost]
        public async Task<IActionResult> ReassignStudent(int id, int newSection)
        {
            var enrollment = await _context.Enrollments.FindAsync(id);
            if (enrollment == null)
            {
                TempData["Error"] = "Student enrollment not found.";
                return RedirectToAction(nameof(ViewStudents));
            }


            var studentsInSection = await _context.Enrollments
                .CountAsync(e => e.GradeLevel == enrollment.GradeLevel &&
                               e.Section == newSection &&
                               e.Status == "approved");

            if (studentsInSection >= 40)
            {
                TempData["Error"] = $"Section {newSection} is full (40/40 students). Please choose another section.";
                return RedirectToAction(nameof(ReassignStudent), new { id });
            }

            var oldSection = enrollment.Section;
            enrollment.Section = newSection;
            enrollment.Status = "approved";
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Student {enrollment.StudentName} has been reassigned from Section {oldSection} to Section {newSection}.";

            return RedirectToAction(nameof(ViewSectionStudents), new { gradeLevel = enrollment.GradeLevel, section = newSection });
        }


        [HttpGet]
        public async Task<IActionResult> GetAvailableSections(string gradeLevel)
        {
            var takenSections = await _context.Users
                .Where(u => u.Role == "professor" &&
                           u.AssignedGradeLevel == gradeLevel &&
                           u.AssignedSection.HasValue)
                .Select(u => new
                {
                    section = u.AssignedSection.Value,
                    professorName = u.FullName
                })
                .ToListAsync();

            return Json(new { takenSections });
        }
    }
}